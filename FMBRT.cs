using System;
using System.Threading;
using System.IO;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Collections.Generic;
using System.Media;

namespace fmdevtools
{
    class FMBRT
    {
        public const string VERSION = "0.1.1";

        private static ExecutionInfo[] infos;

        private static SoundPlayer player;

        static private Dictionary<string, string> GetSettings(string[] args)
        {
            Dictionary<string, string> settings = new Dictionary<string, string>();
            if (File.Exists("FMBRT.ini")) {
                foreach (var row in File.ReadAllLines("FMBRT.ini"))
                {
                    if (row.IndexOf('=') > 0 && row.IndexOf(';') != 0)
                    {
                        string key = row.Split('=')[0];
                        string val = row.Substring(row.IndexOf('=') + 1);

                        if (key == "pass")
                        {
                            Console.WriteLine("INI: " + key + " : **********");
                        } else
                        {
                            Console.WriteLine("INI: " + key + " :" + val);
                        }

                        settings.Add(key, val);
                    }
                }
            }

            string dmtPath;
            if (!settings.ContainsKey("fmdtPath")) {
                dmtPath = FindFMDT(args);
                if (dmtPath == null) {
                    Console.WriteLine("failed to find FMDeveloperTool");
                    Console.WriteLine("Please download the FileMaker Developer Tool https://support.claris.com/s/article/Claris-FileMaker-Developer-Tool?language=en_US");
                    Environment.Exit(-10);
                }
                settings.Add("fmdtPath", dmtPath);
            } else {
                dmtPath = settings["fmdtPath"];
            }
            UpdateChecks.checkForUpdates(dmtPath).ConfigureAwait(false);

            Console.WriteLine("Found Data Migration tool at - " + dmtPath);
            Console.WriteLine("");

            if (!settings.ContainsKey("processCnt"))
            {
                int procCnt = getProccessCount();
                settings.Add("processCnt", procCnt.ToString());
            }

            if (!settings.ContainsKey("user"))
            {
                Console.Write("Username: ");
                string username = Console.ReadLine();
                settings.Add("user", username);
            }

            if (!settings.ContainsKey("pass"))
            {
                Console.Write("Password: ");
                string password = ReadPassword();
                Console.Write("\n");
                settings.Add("pass", password);
            }

            if (!settings.ContainsKey("fmbrtArgs"))
            {
                Console.Write("Additional FMDT args: ");
                string extraArgs = Console.ReadLine();
                settings.Add("fmbrtArgs", extraArgs);
            }

            return settings;
        }


        static void Main(string[] args)
        {
            Console.WriteLine(" Version " + FMBRT.VERSION);
            Console.WriteLine("(c) 2024 Soliant Consulting, Inc");
            Console.WriteLine("Instructions:");
            Console.WriteLine("Needs Source folder in current folder.");
            Console.WriteLine("Needs FMDT exe in subfolder of current/parent/sibling folder.");
            Console.WriteLine("You can optionally specify FMDT exe path as the only argument.");
            Console.WriteLine("Prompts for process count (how many concurrent copies of FMDT to run).");
            Console.WriteLine("Prompts for account name and password.");
            Console.WriteLine("Prompts for file prefix/suffix.");
            Console.WriteLine("Automatically specifies these FMDT arguments;");
            Console.WriteLine("	-p, -u, -target_filename");
            Console.WriteLine("Prompts for additional FMDT arguments which can be any of these (space delimited);");
            Console.WriteLine("	-encryption_key ");
            Console.WriteLine("	-skipSchema, -skipStructure, -rebuildIndex ");
            Console.WriteLine("Gets list of databases in Source and runs FMDT recovery on each file.");
            Console.WriteLine("The files are proccessed in descending size.");
            Console.WriteLine("Creates log file for each target and a summary log file.");
            Console.WriteLine("");

            Dictionary<string, string> settings = GetSettings(args);
            InitializeSound();
            CheckDirectoriesExist();

            string[] files = Directory.GetFiles("source", FMBRT.FindFileWildcard(settings), SearchOption.TopDirectoryOnly);
            Array.Sort(files, (x, y) => new FileInfo(y).Length.CompareTo(new FileInfo(x).Length));
            infos = new ExecutionInfo[files.Length];

            int fileIndex = 0;

            int procCnt = Int32.Parse(settings["processCnt"]);
            Worker[] workers = new Worker[procCnt];
            bool allDone = false;
            bool keepRunning = true;

            // if someone hits ctrl+c kill all the workers
            Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e) {
                fileIndex = files.Length;
                keepRunning = false;
                e.Cancel = true;
                Console.WriteLine("Ctrl+C pressed, exiting...");

                for (int i = 0; i < procCnt; i++) {
                    if (workers[i] != null && workers[i].isRunning()) {
                        workers[i].Dispose();
                    }
                }
                Environment.Exit(-4);
            };

            DateTime startTime = DateTime.Now;
            while (keepRunning) {
                for (int i = 0; i < procCnt; i++) {
                    //launch new workers if we can
                    if (workers[i] == null && fileIndex < files.Length) {
                        workers[i] = GetWorker(files[fileIndex], settings, fileIndex++);
                        if (workers[i] != null) {
                            workers[i].start();
                        }
                    }
                    //clean out finished workers
                    if (workers[i] != null && !workers[i].isRunning()) {
                        Worker w = workers[i];
                        infos[w.fileNumber] = new ExecutionInfo(w.file, w.fileSize, w.startTime, w.endTime, i, w.fileNumber);
                        workers[i] = null;
                    }
                }

                if (fileIndex >= files.Length) {
                    allDone = true;
                    for (int i = 0; i < procCnt; i++) {
                        if (workers[i] != null) {
                            allDone = false;
                            break;
                        }
                    }
                }
                if (allDone) {
                    // we have finished!
                    break;
                }
                Thread.Sleep(100);
            }

            var outputStream = new FileStream("recovered" + Path.DirectorySeparatorChar + "Recover.log", FileMode.Create);
            for (int i = 0; i < files.Length; i++) {
                string recoveredPath = "recovered" + Path.DirectorySeparatorChar + i;
                Console.WriteLine("Path: " + recoveredPath);
                if (Directory.Exists(recoveredPath)) {
                    foreach (string filePath in Directory.GetFiles(recoveredPath)) {
                        string fileName = Path.GetFileName(filePath);
                        Console.WriteLine("filePath: " + filePath);
                        string destFilePath = Path.Combine("recovered", fileName);
                        if (fileName == "Recover.log") {
                            var inputStream = new FileStream(filePath, FileMode.Open);
                            inputStream.CopyTo(outputStream);
                            inputStream.Close();
                            string fmFileName = Path.GetFileName(files[i]);
                            destFilePath = Path.Combine("recovered", Path.ChangeExtension(fmFileName, "log"));
                        }
                        if (File.Exists(destFilePath)) {
                            File.Delete(destFilePath);
                        }
                        Console.WriteLine("destFilePath: " + destFilePath);
                        File.Move(filePath, destFilePath);
                    }
                    Directory.Delete(recoveredPath, true);
                }
            }
            outputStream.Close();

            DateTime endTime = DateTime.Now;

            string outFile = "FMBRT.log";
            string output = "";
            string header = "#\tProces\tFile\tSize(b)\tstart\tfinish\tduration";
            if (!File.Exists(outFile)) {
                File.Create(outFile).Dispose();
                output = header + "\r\n";
            } else {
                Console.WriteLine(header);
            }


            int fileCnt = 0;
            int procCntUsed = 0;
            long totalFileSize = 0;


            for (int i = 0; i < infos.Length; i++) {
                if (infos[i] != null) {
                    fileCnt++;
                    if (infos[i].threadNumber > procCntUsed) {
                        procCntUsed = infos[i].threadNumber;
                    }
                    totalFileSize += infos[i].fileSize;
                    output += infos[i].ToString() + "\r\n";
                }
            }
            output += "----\t----\t--------\t--------\t--------\t--------\t--------\r\n";
            TimeSpan d = endTime - startTime;
            output += fileCnt + "\t" + (procCntUsed+1) + "\t-\t" + totalFileSize + "\t" + startTime.ToString("HH:mm:ss.f") +
                    "\t" + endTime.ToString("HH:mm:ss.f") + "\t" + d.ToString() + "\r\n";
            output += "----\t----\t--------\t--------\t--------\t--------\t--------\r\n";
            Console.WriteLine(output);
            File.AppendAllText(outFile,output);

            done();
        }

        private static void CheckDirectoriesExist() {
            if (!Directory.Exists("source")) {
                Directory.CreateDirectory("source");
            }
            if (!Directory.Exists("recovered")) {
                Directory.CreateDirectory("recovered");
            }
        }

        private static string FindFileWildcard(Dictionary<string, string> settings) {
            String pattern = "*.fmp12";
            return pattern;
        }

        private static string FindFMDT(string [] args) {
            if (args.Length > 0 && File.Exists(args[0])) {
                return args[0];
            }

            try {
                String DMTExe = "FMDeveloperTool.exe";
                if (Path.DirectorySeparatorChar == '/') {
                    DMTExe = "FMDeveloperTool";
                }

                Console.WriteLine("Searching for FMDT");
                string[] files = Directory.GetFiles("." + Path.DirectorySeparatorChar, DMTExe, SearchOption.AllDirectories);
                if (files.Length > 0) {
                    return files[0];
                }

                files = Directory.GetFiles(".." + Path.DirectorySeparatorChar, DMTExe, SearchOption.AllDirectories);
                if (files.Length > 0) {
                    return files[0];
                }
            } catch (Exception){
            }

            return null;
        }


        private static Worker GetWorker(string file, Dictionary<string, string> settings, int fileIndex)
        {
            string regex = FMBRT.FileRegex(settings);

            Regex r = new Regex(regex, RegexOptions.IgnoreCase);
            Match m = r.Match(file);
            Group g = m.Groups[1];
            String baseName = g.ToString();
            string recoveredPath = "recovered" + Path.DirectorySeparatorChar + fileIndex;
            if (!Directory.Exists(recoveredPath)) {
                Directory.CreateDirectory(recoveredPath);
            }
            String target = recoveredPath + Path.DirectorySeparatorChar + baseName + ".fmp12";

            return new Worker(baseName, file, target,  settings["user"], settings["pass"], settings["fmdtPath"], settings["fmbrtArgs"], fileIndex);
        }

        private static string FileRegex(Dictionary<string, string> settings)
        {
            String regex = "(.*).fmp12";
            if (
                settings.ContainsKey("sourceExtra")
                && settings["sourceExtra"] != ""
            ) {
                if (settings["hasPrefix"] == "p") {
                    regex = settings["sourceExtra"] + regex;
                }
                else if (settings["hasPrefix"] == "s") {
                    regex = "(.*)"+settings["sourceExtra"]+".fmp12";
                }
            }

            regex = "source\\\\" + regex;
            if (Path.DirectorySeparatorChar != '\\') {
                regex = "source/" + regex;
            }

            return regex;
        }

        private static int getProccessCount()
        {
            bool firstRun = true;
            int result = -1;
            while (result < 1 || result > 10) {
                if (!firstRun) {
                    Console.WriteLine("Please enter a number between 1 and 10 inclusive");
                    firstRun = false;
                }
                Console.Write("Process Count: ");
                string procCnt = Console.ReadLine();
                try
                {
                    result = Int32.Parse(procCnt);
                } catch (FormatException){}

            }
            return result;
        }


        private enum StdHandle
        {
            Input = -10,
            Output = -11,
            Error = -12,
        }

        private enum ConsoleMode
        {
            ENABLE_ECHO_INPUT = 4
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(StdHandle nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out int lpMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, int dwMode);

        public static string ReadPassword()
        {
            if (Path.DirectorySeparatorChar == '/') {
                return Console.ReadLine();
            }
            IntPtr stdInputHandle = GetStdHandle(StdHandle.Input);
            if (stdInputHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("No console input");
            }

            int previousConsoleMode;
            if (!GetConsoleMode(stdInputHandle, out previousConsoleMode))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not get console mode.");
            }

            // disable console input echo
            if (!SetConsoleMode(stdInputHandle, previousConsoleMode & ~(int)ConsoleMode.ENABLE_ECHO_INPUT))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not disable console input echo.");
            }

            // just read the password using standard Console.ReadLine()
            string password = Console.ReadLine();

            // reset console mode to previous
            if (!SetConsoleMode(stdInputHandle, previousConsoleMode))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not reset console mode.");
            }

            return password;
        }

        private static void InitializeSound()
        {
            // Create an instance of the SoundPlayer class.
            player = new SoundPlayer();
            player.SoundLocation = "C:\\Windows\\media\\Alarm01.wav";
            player.Load();
        }

        public static void done()
        {
            if (Path.DirectorySeparatorChar == '\\') {
                player.PlaySync();
            } else {
                Console.Beep(800, 200);
            }
        }
    }

    class ExecutionInfo {
        public string fileName { get; private set; }
        public long fileSize { get; private set; }
        public DateTime startTime { get; private set; }
        public DateTime endTime { get; private set; }
        public int threadNumber { get; private set; }
        public int fileNumber { get; private set; }

        public ExecutionInfo(string fileName, long FileSize, DateTime startTime, DateTime endTime, int threadNumber, int fileNumber) {
            this.fileName = fileName;
            this.fileSize = FileSize;
            this.startTime = startTime;
            this.endTime = endTime;
            this.threadNumber = threadNumber;
            this.fileNumber = fileNumber;
        }

        public override string ToString() {
            TimeSpan d = this.endTime - this.startTime;
            return (this.fileNumber+1) + "\t" + (this.threadNumber+1) + "\t" + this.fileName + "\t" + this.fileSize + "\t"
                + formatDate(this.startTime) + "\t" + formatDate(this.endTime) + "\t" + d.ToString();
        }

        private string formatDate(DateTime date) {
            return date.ToString("HH:mm:ss.f");
        }
    }
}
