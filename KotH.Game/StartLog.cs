using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace KotH.Client
{
    /// <summary>Always on: every start's phases in milliseconds since launch, written through at once to
    /// ~/.local/share/KotH/logs/start-*.log (the last ten kept), so a slow or silent start can be read
    /// afterwards. Stays open for the session: compiles and audio trouble later in play land there too.
    /// Thread-safe; never throws (a log it cannot write is just missing).</summary>
    public static class StartLog
    {
        const int Kept = 10;

        static readonly Stopwatch clock = new Stopwatch();
        static readonly object gate = new object();
        static StreamWriter file;

        /// <summary>Milliseconds since Begin.</summary>
        public static long Now => clock.ElapsedMilliseconds;

        /// <summary>The file written to; null when it could not be made.</summary>
        public static string Path { get; private set; }

        /// <summary>First thing in main: starts the clock and a new file.</summary>
        public static void Begin(string[] args)
        {
            if (clock.IsRunning)
                return;
            clock.Start();
            try
            {
                string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KotH", "logs");
                Directory.CreateDirectory(folder);
                string name = $"start-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log";
                Path = System.IO.Path.Combine(folder, name);
                file = new StreamWriter(new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
                foreach (string old in Directory.GetFiles(folder, "start-*.log").OrderByDescending(f => f).Skip(Kept))
                    File.Delete(old);
            }
            catch (Exception e)
            {
                file = null;
                Console.WriteLine($"[start] no start log: {e.Message}");
            }
            double launched = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            Write($"KotH {DateTime.Now:yyyy-MM-dd HH:mm:ss}, args \"{string.Join(' ', args)}\", main {launched:F0} ms after launch");
        }

        /// <summary>A moment of the start, with what is known then.</summary>
        public static void Mark(string what) => Write($"{Now,7} {what}");

        /// <summary>A step that began at <paramref name="since"/> (a Now) and ends now.</summary>
        public static void Took(string what, long since, string detail = null)
            => Write($"{Now,7} {what}: {Now - since} ms{(detail != null ? ", " + detail : "")}");

        /// <summary>Something wrong: in the file and on the terminal.</summary>
        public static void Warn(string what)
        {
            Console.WriteLine($"[start] {what}");
            Write($"{Now,7} WARNING {what}");
        }

        static void Write(string line)
        {
            lock (gate)
            {
                try
                {
                    file?.WriteLine(line);
                }
                catch (Exception)
                {
                    file = null;
                }
            }
        }
    }
}
