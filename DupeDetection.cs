using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using DarkBot;
using DarkBotBackup;
using Discord;
using Discord.WebSocket;
using SystemImage = System.Drawing.Image;
using SystemColor = System.Drawing.Color;

namespace DarkBot.DupeDetection
{
    [BotModuleDependency(new Type[] { typeof(Backup) })]
    public class DupeDetection : BotModule
    {
        private DiscordSocketClient _client;
        private Backup _backup;
        private Dictionary<string, string> hashes = new Dictionary<string, string>();
        private Dictionary<ulong, ulong> channel_to_server = new Dictionary<ulong, ulong>();
        private Dictionary<ulong, Dictionary<string, string>> reverse_hashes = new Dictionary<ulong, Dictionary<string, string>>();
        private string backupPath = Path.Combine(Environment.CurrentDirectory, "Backup");
        private string dupePath = Path.Combine(Environment.CurrentDirectory, "Duplicate");
        private string dupeDBPath = Path.Combine(Environment.CurrentDirectory, "Duplicate", "hashes.txt");
        private ConcurrentQueue<string> hashQueue = new ConcurrentQueue<string>();
        private ConcurrentQueue<NotifyMessage> notifyQueue = new ConcurrentQueue<NotifyMessage>();

        private const int RESIZE_HEIGHT = 32;
        private const int RESIZE_WIDTH = 32;
        private bool ready;


        public Task Initialize(IServiceProvider services)
        {
            if (!Directory.Exists(backupPath))
            {
                Directory.CreateDirectory(backupPath);
            }
            if (!Directory.Exists(dupePath))
            {
                Directory.CreateDirectory(dupePath);
            }
            _client = (DiscordSocketClient)services.GetService(typeof(DiscordSocketClient));
            _client.Ready += OnReady;
            _backup = (Backup)services.GetService(typeof(Backup));
            _backup.PictureEvent += CheckPicture;
            Task lph = Task.Run(LoadPictureHashes);
            Task hs = Task.Run(HashingService);
            Task ns = Task.Run(NotifyService);
            return Task.CompletedTask;
        }

        public Task OnReady()
        {
            ready = true;
            return Task.CompletedTask;
        }

        public void LoadPictureHashes()
        {
            if (!File.Exists(dupeDBPath))
            {
                File.WriteAllText(dupeDBPath, "");
            }
            using (StreamReader sr = new StreamReader(dupeDBPath))
            {
                hashes.Clear();
                string currentLine = null;
                while ((currentLine = sr.ReadLine()) != null)
                {
                    currentLine = currentLine.Trim();
                    string lhs = currentLine.Substring(0, currentLine.IndexOf("="));
                    string rhs = currentLine.Substring(currentLine.IndexOf("=") + 1);
                    hashes[lhs] = rhs;
                    ulong channelID = GetChannelIDFromPath(lhs);
                    if (!reverse_hashes.ContainsKey(channelID))
                    {
                        reverse_hashes.Add(channelID, new Dictionary<string, string>());
                    }
                    if (!reverse_hashes[channelID].ContainsKey(rhs))
                    {
                        reverse_hashes[channelID][rhs] = lhs;
                    }
                    else
                    {
                        string existingFile = reverse_hashes[channelID][rhs];
                        Console.WriteLine($"Dupe (loaded): {lhs} matches {existingFile}");
                    }
                }
            }
            AddMissingHashes();
        }

        public void SavePictureHashes()
        {
            if (File.Exists(dupeDBPath))
            {
                File.Delete(dupeDBPath);
            }
            using (StreamWriter sw = new StreamWriter(dupeDBPath))
            {
                foreach (KeyValuePair<string, string> kvp in hashes)
                {
                    sw.WriteLine($"{kvp.Key}={kvp.Value}");
                }
            }
        }

        public void AddMissingHashes()
        {
            string[] backupFiles = Directory.GetFiles(backupPath, "*", SearchOption.AllDirectories);
            foreach (string backupFile in backupFiles)
            {
                string backupFileClipped = backupFile.Substring(backupFile.LastIndexOf("Backup"));
                if (!hashes.ContainsKey(backupFileClipped) && (backupFileClipped.EndsWith(".jpg") || backupFileClipped.EndsWith(".png")))
                {
                    hashQueue.Enqueue(backupFileClipped);
                }
            }
        }

        public async void NotifyService()
        {
            while (!ready)
            {
                await Task.Delay(1000);
            }
            while (true)
            {
                await Task.Delay(1000);
                if (notifyQueue.TryDequeue(out NotifyMessage notifyMessage))
                {
                    ulong channelID = notifyMessage.channelID;
                    ulong originalMessageID = notifyMessage.originalMessageID;
                    ulong repostMessageID = notifyMessage.repostMessageID;
                    ulong serverID = 0;
                    if (channel_to_server.ContainsKey(notifyMessage.channelID))
                    {
                        serverID = channel_to_server[notifyMessage.channelID];
                    }
                    else
                    {
                        foreach (SocketGuild findGuild in _client.Guilds)
                        {
                            foreach (SocketGuildChannel findChannel in findGuild.Channels)
                            {
                                if (!channel_to_server.ContainsKey(findChannel.Id))
                                {
                                    channel_to_server[findChannel.Id] = findGuild.Id;
                                }
                            }
                        }
                        if (channel_to_server.ContainsKey(channelID))
                        {
                            serverID = channel_to_server[channelID];
                        }
                        else
                        {
                            Console.WriteLine("Failed to get channel ID");
                        }
                    }
                    if (serverID != 0)
                    {
                        SocketGuild sg = _client.GetGuild(serverID);
                        SocketTextChannel sgc = sg.GetChannel(channelID) as SocketTextChannel;
                        IMessage messageOriginal = await sgc.GetMessageAsync(originalMessageID);
                        IMessage messageNew = await sgc.GetMessageAsync(repostMessageID);
                        foreach (SocketGuildChannel possibleChannel in sg.Channels)
                        {
                            SocketTextChannel possibleTextChannel = possibleChannel as SocketTextChannel;
                            if (possibleTextChannel == null)
                            {
                                continue;
                            }
                            if (possibleTextChannel.Name == "reposts")
                            {
                                await possibleTextChannel.SendMessageAsync($"Original: <{messageOriginal.GetJumpUrl()}>, Repost: <{messageNew.GetJumpUrl()}>");
                                break;
                            }
                        }
                    }
                    await Task.Delay(10000);
                }
            }
        }

        public void HashingService()
        {
            Console.WriteLine("Hashing service started");
            while (true)
            {
                bool newHashes = false;
                while (hashQueue.TryDequeue(out string fileToHash))
                {
                    newHashes = true;
                    Console.WriteLine("Hashing " + fileToHash);
                    SystemImage si = SystemImage.FromFile(fileToHash);
                    Bitmap resizedImage = ResizeImage(si);
                    byte[] picHash = new byte[RESIZE_WIDTH];
                    for (int yPos = 0; yPos < RESIZE_HEIGHT; yPos++)
                    {
                        for (int xPos = 0; xPos < RESIZE_WIDTH; xPos++)
                        {
                            SystemColor testPixel = resizedImage.GetPixel(xPos, yPos);
                            int rVal = testPixel.R / 64;
                            if (rVal > 3)
                            {
                                rVal = 3;
                            }
                            int gVal = testPixel.G / 64;
                            if (gVal > 3)
                            {
                                gVal = 3;
                            }
                            int bVal = testPixel.B / 64;
                            if (bVal > 3)
                            {
                                bVal = 3;
                            }
                            byte totalVal = (byte)((rVal << 4) + (gVal << 2) + rVal);
                            picHash[xPos] = (byte)(picHash[xPos] ^ totalVal);
                        }
                    }
                    string base64String = GetBase64String(picHash);
                    ulong thisChannel = GetChannelIDFromPath(fileToHash);
                    hashes[fileToHash] = base64String;
                    if (!reverse_hashes.ContainsKey(thisChannel))
                    {
                        reverse_hashes.Add(thisChannel, new Dictionary<string, string>());
                    }
                    if (reverse_hashes[thisChannel].ContainsKey(base64String))
                    {
                        string existingFile = reverse_hashes[thisChannel][base64String];
                        NotifyRepost(thisChannel, GetMessageIDFromPath(existingFile), GetMessageIDFromPath(fileToHash));
                        Console.WriteLine($"MATCH {fileToHash}, hash: {base64String}, existing file {existingFile}");
                    }
                    else
                    {
                        reverse_hashes[thisChannel][base64String] = fileToHash;
                    }
                    GC.Collect();
                    Console.WriteLine($"{fileToHash}={base64String}");
                }
                if (newHashes)
                {
                    SavePictureHashes();
                }
                Task.Delay(1000).Wait();
            }
        }

        public void CheckPicture(string picturePath)
        {
            if (picturePath.EndsWith(".jpg") || picturePath.EndsWith(".png"))
            {
                hashQueue.Enqueue(picturePath);
            }
        }

        public string GetBase64String(byte[] input)
        {
            return System.Convert.ToBase64String(input);
        }

        public static ulong GetChannelIDFromPath(string filePath)
        {
            string[] channelIDString = filePath.Split(Path.DirectorySeparatorChar);
            return ulong.Parse(channelIDString[1]);
        }
        public static ulong GetMessageIDFromPath(string filePath)
        {
            string[] channelIDString = filePath.Split(Path.DirectorySeparatorChar);
            string messagePart = channelIDString[2];
            string messageID = messagePart.Split('-')[0];
            return ulong.Parse(messageID);
        }
        public static Bitmap ResizeImage(SystemImage image)
        {
            var destRect = new Rectangle(0, 0, RESIZE_WIDTH, RESIZE_HEIGHT);
            var destImage = new Bitmap(RESIZE_WIDTH, RESIZE_HEIGHT);

            destImage.SetResolution(32, 32);

            using (var graphics = Graphics.FromImage(destImage))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

                using (var wrapMode = new ImageAttributes())
                {
                    wrapMode.SetWrapMode(WrapMode.TileFlipXY);
                    graphics.DrawImage(image, destRect, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, wrapMode);
                }
            }

            return destImage;
        }

        public void NotifyRepost(ulong channelID, ulong originalMessage, ulong newMessage)
        {
            NotifyMessage nm = new NotifyMessage(channelID, originalMessage, newMessage);
            notifyQueue.Enqueue(nm);
        }
    }
}
