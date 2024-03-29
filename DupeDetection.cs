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
        private Whitelist.Whitelist _whitelist;
        private List<string> whitelistObjects = new List<string>();
        private ConcurrentDictionary<ulong, ulong> channelCategoryCache = new ConcurrentDictionary<ulong, ulong>();
        private Dictionary<string, string> hashes = new Dictionary<string, string>();
        private Dictionary<ulong, ulong> channel_to_server = new Dictionary<ulong, ulong>();
        private Dictionary<ulong, Dictionary<string, string>> reverse_hashes = new Dictionary<ulong, Dictionary<string, string>>();
        private string backupPath = Path.Combine(Environment.CurrentDirectory, "Backup");
        private string dupePath = Path.Combine(Environment.CurrentDirectory, "Duplicate");
        private ConcurrentQueue<string> hashQueue = new ConcurrentQueue<string>();
        private ConcurrentQueue<NotifyMessage> notifyQueue = new ConcurrentQueue<NotifyMessage>();

        private const int RESIZE_HEIGHT = 32;
        private const int RESIZE_WIDTH = 32;
        private bool ready;
        private bool loadHashesDone;
        private bool missingHashesDone;


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
            _whitelist = services.GetService(typeof(Whitelist.Whitelist)) as Whitelist.Whitelist;
            _client = services.GetService(typeof(DiscordSocketClient)) as DiscordSocketClient;
            _client.Ready += OnReady;
            _client.MessageReceived += MessageReceived;
            _client.MessageDeleted += MessageDeleted;
            _backup = services.GetService(typeof(Backup)) as Backup;
            _backup.PictureEvent += CheckPicture;
            Task lph = Task.Run(LoadPictureHashes);
            Task hs = Task.Run(HashingService);
            Task ns = Task.Run(NotifyService);
            Task amh = Task.Run(AddMissingHashes);
            LoadWhitelist();
            return Task.CompletedTask;
        }

        public Task OnReady()
        {
            ready = true;
            return Task.CompletedTask;
        }

        public async Task MessageReceived(SocketMessage message)
        {
            SocketTextChannel stc = message.Channel as SocketTextChannel;
            if (message.Author.IsBot || stc == null)
            {
                return;
            }
            if (!stc.GetUser(message.Author.Id).GuildPermissions.ManageChannels)
            {
                return;
            }
            if (message.Content.StartsWith(".duplicate add "))
            {
                string key = message.Content.Substring(15);
                whitelistObjects.Add(key);
                SaveWhitelist();
                await stc.SendMessageAsync($"Duplicate detector is now watching {key}");
            }
        }

        public Task MessageDeleted(Cacheable<IMessage, ulong> cacheable, Cacheable<IMessageChannel, ulong> channel)
        {
            if (cacheable.HasValue)
            {
                Log(LogSeverity.Info, "Message deleted: " + cacheable.Value.Id);
                DeleteID(cacheable.Id);
            }
            return Task.CompletedTask;
        }

        private void LoadWhitelist()
        {
            string whitelistString = DataStore.Load("DuplicateWhitelist");
            if (whitelistString == null)
            {
                return;
            }
            whitelistObjects.Clear();
            using (StringReader sr = new StringReader(whitelistString))
            {
                string currentLine = null;
                while ((currentLine = sr.ReadLine()) != null)
                {
                    whitelistObjects.Add(currentLine);
                }
            }
        }

        private void SaveWhitelist()
        {
            StringBuilder sb = new StringBuilder();
            foreach (string writeLine in whitelistObjects)
            {
                sb.AppendLine(writeLine);
            }
            DataStore.Save("DuplicateWhitelist", sb.ToString());
        }

        public void LoadPictureHashes()
        {
            string duplicateHashes = DataStore.Load("DuplicateHashes");
            if (duplicateHashes == null)
            {
                loadHashesDone = true;
                return;
            }
            using (StringReader sr = new StringReader(duplicateHashes))
            {
                hashes.Clear();
                reverse_hashes.Clear();
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
                        Log(LogSeverity.Info, $"Dupe (loaded): {lhs} matches {existingFile}");
                    }
                }
            }
            loadHashesDone = true;
        }

        public void SavePictureHashes()
        {
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, string> kvp in hashes)
            {
                sb.AppendLine($"{kvp.Key}={kvp.Value}");
            }
            DataStore.Save("DuplicateHashes", sb.ToString());
        }

        public async void AddMissingHashes()
        {
            while (!ready)
            {
                Log(LogSeverity.Debug, "Missing hashes waiting for ready.");
                await Task.Delay(1000);
            }
            while (!loadHashesDone)
            {
                Log(LogSeverity.Debug, "Missing hashes waiting for load.");
                await Task.Delay(1000);
            }
            Dictionary<ulong, SocketTextChannel> channelCache = new Dictionary<ulong, SocketTextChannel>();
            foreach (SocketGuild sg in _client.Guilds)
            {
                foreach (SocketChannel sc in sg.Channels)
                {
                    SocketTextChannel stc = sc as SocketTextChannel;
                    if (stc != null)
                    {
                        channelCache.Add(stc.Id, stc);
                    }
                }
            }
            string[] backupFiles = Directory.GetFiles(backupPath, "*", SearchOption.AllDirectories);
            foreach (string backupFile in backupFiles)
            {
                if (backupFile.EndsWith(".txt"))
                {
                    continue;
                }
                string backupFileClipped = backupFile.Substring(backupFile.LastIndexOf("Backup") + 7);
                ulong channelID = GetChannelIDFromPath(backupFileClipped);
                if (channelCache.ContainsKey(channelID))
                {
                    SocketTextChannel stc = channelCache[channelID];
                    ulong categoryID = GetCategory(stc);
                    if (CheckWhitelist(stc.Id) || CheckWhitelist(categoryID))
                    {
                        if (!hashes.ContainsKey(backupFileClipped) && (backupFileClipped.EndsWith(".jpg") || backupFileClipped.EndsWith(".png")))
                        {
                            hashQueue.Enqueue(backupFile);
                        }
                    }
                }
            }
            missingHashesDone = true;
        }

        public async void NotifyService()
        {
            while (!ready)
            {
                Log(LogSeverity.Debug, "Notify waiting for ready.");
                await Task.Delay(1000);
            }
            while (!missingHashesDone || hashQueue.Count > 0)
            {
                Log(LogSeverity.Debug, "NotifyService waiting for missing hashes to complete.");
                await Task.Delay(1000);
            }
            while (true)
            {
                await Task.Delay(1000);
                if (notifyQueue.TryDequeue(out NotifyMessage notifyMessage))
                {
                    Log(LogSeverity.Info, "Message dequeued, left: " + notifyQueue.Count);
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
                            Log(LogSeverity.Warning, "Failed to get channel ID");
                        }
                    }
                    if (serverID != 0)
                    {
                        SocketGuild sg = _client.GetGuild(serverID);
                        SocketTextChannel sgc = sg.GetChannel(channelID) as SocketTextChannel;
                        IMessage messageOriginal = await sgc.GetMessageAsync(originalMessageID);
                        IMessage messageNew = await sgc.GetMessageAsync(repostMessageID);
                        if (messageOriginal != null && messageNew != null)
                        {
                            bool repostFound = false;
                            foreach (SocketGuildChannel possibleChannel in sg.Channels)
                            {
                                SocketTextChannel possibleTextChannel = possibleChannel as SocketTextChannel;
                                if (possibleTextChannel == null)
                                {
                                    continue;
                                }
                                if (possibleTextChannel.Name == "reposts")
                                {
                                    repostFound = true;
                                    Log(LogSeverity.Info, "Posting in reposts channel");
                                    await possibleTextChannel.SendMessageAsync($"Original: <{messageOriginal.GetJumpUrl()}>, Repost: <{messageNew.GetJumpUrl()}>");
                                    break;
                                }
                            }
                            if (!repostFound)
                            {
                                Log(LogSeverity.Warning, "Failed to find reposts channel");
                            }
                        }
                        else
                        {
                            if (messageOriginal == null)
                            {
                                Log(LogSeverity.Warning, $"Message original is null, ID {originalMessageID}");
                                DeleteID(originalMessageID);
                            }
                            if (messageNew == null)
                            {
                                Log(LogSeverity.Warning, $"Message new is null, ID: {repostMessageID}");
                                DeleteID(repostMessageID);
                            }
                        }
                    }
                    else
                    {
                        Log(LogSeverity.Warning, "Server ID is zero.");
                    }
                    await Task.Delay(10000);
                }
            }
        }

        public void DeleteID(ulong messageID)
        {
            string messageIdString = messageID.ToString();
            string[] files = Directory.GetFiles(backupPath, "*", SearchOption.AllDirectories);
            foreach (string checkFile in files)
            {
                if (checkFile.Contains(messageIdString))
                {
                    File.Delete(checkFile);
                    string backupFileClipped = checkFile.Substring(checkFile.IndexOf("Backup") + 7);
                    Log(LogSeverity.Info, $"Deleting {backupFileClipped}");
                    if (hashes.ContainsKey(backupFileClipped))
                    {
                        hashes.Remove(backupFileClipped);
                        SavePictureHashes();
                        LoadPictureHashes();
                    }
                }
            }
        }

        public void HashingService()
        {
            Log(LogSeverity.Info, "Hashing service started");
            while (true)
            {
                bool newHashes = false;
                while (hashQueue.TryDequeue(out string fileToHash))
                {
                    if (!File.Exists(fileToHash))
                    {
                        Log(LogSeverity.Warning, "File to hash is missing: " + fileToHash);
                        continue;
                    }
                    newHashes = true;
                    Log(LogSeverity.Info, "Hashing " + fileToHash);
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
                    string shortName = fileToHash.Substring(fileToHash.IndexOf("Backup") + 7);
                    hashes[shortName] = base64String;
                    if (!reverse_hashes.ContainsKey(thisChannel))
                    {
                        reverse_hashes.Add(thisChannel, new Dictionary<string, string>());
                    }
                    if (reverse_hashes[thisChannel].ContainsKey(base64String))
                    {
                        string existingFile = reverse_hashes[thisChannel][base64String];
                        ulong existingID = GetMessageIDFromPath(existingFile);
                        ulong newID = GetMessageIDFromPath(fileToHash);
                        if (existingID != newID)
                        {
                            NotifyRepost(thisChannel, existingID, newID);
                            Log(LogSeverity.Info, $"MATCH {shortName}, hash: {base64String}, existing file {existingFile}");
                        }
                    }
                    else
                    {
                        reverse_hashes[thisChannel][base64String] = fileToHash;
                    }
                    GC.Collect();
                    Log(LogSeverity.Info, $"{shortName}={base64String}");
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
            if (!picturePath.EndsWith(".jpg") && !picturePath.EndsWith(".png"))
            {
                return;
            }
            ulong channelID = GetChannelIDFromPath(picturePath);
            SocketTextChannel checkChannel = null;
            //Cache this somewhere eventually
            foreach (SocketGuild sg in _client.Guilds)
            {
                foreach (SocketChannel sc in sg.Channels)
                {
                    SocketTextChannel stc = sc as SocketTextChannel;
                    if (stc != null && stc.Id == channelID)
                    {
                        checkChannel = stc;
                        break;
                    }
                }
                if (checkChannel != null)
                {
                    break;
                }
            }
            if (checkChannel == null)
            {
                return;
            }
            ulong categoryID = GetCategory(checkChannel);
            if (CheckWhitelist(checkChannel.Id) || CheckWhitelist(categoryID))
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
            return ulong.Parse(channelIDString[channelIDString.Length - 2]);
        }
        public static ulong GetMessageIDFromPath(string filePath)
        {
            string[] channelIDString = filePath.Split(Path.DirectorySeparatorChar);
            string messagePart = channelIDString[channelIDString.Length - 1];
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
            Log(LogSeverity.Info, "Message queued, new amount: " + notifyQueue.Count);
            notifyQueue.Enqueue(nm);
        }

        private ulong GetCategory(SocketTextChannel textChannel)
        {
            if (channelCategoryCache.ContainsKey(textChannel.Id))
            {
                return channelCategoryCache[textChannel.Id];
            }
            ulong retVal = 0;
            foreach (SocketCategoryChannel scc in textChannel.Guild.CategoryChannels)
            {
                foreach (SocketChannel sc in scc.Channels)
                {
                    channelCategoryCache[sc.Id] = scc.Id;
                    if (textChannel.Id == sc.Id)
                    {
                        retVal = scc.Id;
                    }
                }
            }
            return retVal;
        }

        private bool CheckWhitelist(ulong id)
        {
            foreach (string checkKey in whitelistObjects)
            {
                if (_whitelist.ObjectOK(checkKey, id))
                {
                    return true;
                }
            }
            return false;
        }

        private void Log(LogSeverity severity, string text)
        {
            LogMessage logMessage = new LogMessage(severity, "DupeDetection", text);
            Program.LogAsync(logMessage);
        }
    }
}
