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
        private Dictionary<string, string> reverse_hashes = new Dictionary<string, string>();
        private string backupPath = Path.Combine(Environment.CurrentDirectory, "Backup");
        private string dupePath = Path.Combine(Environment.CurrentDirectory, "Duplicate");
        private string dupeDBPath = Path.Combine(Environment.CurrentDirectory, "Duplicate", "hashes.txt");
        private ConcurrentQueue<string> hashQueue = new ConcurrentQueue<string>();

        private const int RESIZE_HEIGHT = 32;
        private const int RESIZE_WIDTH = 32;


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
            _backup = (Backup)services.GetService(typeof(Backup));
            _backup.PictureEvent += CheckPicture;
            Task lph = Task.Run(LoadPictureHashes);
            Task hs = Task.Run(HashingService);
            hs.Wait();
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
                    if (!reverse_hashes.ContainsKey(rhs))
                    {
                        reverse_hashes[rhs] = lhs;
                    }
                    else
                    {
                        Console.WriteLine($"Dupe: {lhs} matches {reverse_hashes[rhs]}");
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
                    hashes[fileToHash] = base64String;
                    if (reverse_hashes.ContainsKey(base64String))
                    {
                        Console.WriteLine($"MATCH {fileToHash}, hash: {base64String}, existing file {reverse_hashes[base64String]}");
                    }
                    else
                    {
                        reverse_hashes[base64String] = fileToHash;
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
    }
}
