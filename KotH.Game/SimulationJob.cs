using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace KotH.Client
{
    /// <summary>The frame's simulation ticks on a thread of their own while the game thread draws: the
    /// driver hands them over in Update (Schedule), the compositor starts them once the entity processors
    /// are done (Start), the next Update waits for them first (Join). Never both threads at once, so the
    /// world needs no locks; every handover is a barrier. Without a draw Join runs them itself.</summary>
    public static class SimulationJob
    {
        static readonly SemaphoreSlim go = new SemaphoreSlim(0);
        static readonly ManualResetEventSlim done = new ManualResetEventSlim(true);
        static Thread thread;
        static Action scheduled, running;
        static ExceptionDispatchInfo failed;

        /// <summary>Update: the work for this frame (a cached delegate, nothing allocated per frame).</summary>
        public static void Schedule(Action work) => scheduled = work;

        /// <summary>Forgets the scheduled work: the world it would step is being taken down.</summary>
        public static void Drop() => scheduled = null;

        /// <summary>Draw, after the entity processors: hands the scheduled work to the thread.</summary>
        public static void Start()
        {
            Action work = scheduled;
            if (work == null)
                return;
            scheduled = null;
            if (thread == null)
            {
                thread = new Thread(Run) { Name = "KotH Simulation", IsBackground = true };
                thread.Start();
            }
            running = work;
            done.Reset();
            go.Release();
        }

        /// <summary>First thing in the game's Update, and before anything else reads the world after a
        /// draw: waits for the work, runs it here if no draw started it, throws what it threw.</summary>
        public static void Join()
        {
            done.Wait();
            if (scheduled is { } late)
            {
                scheduled = null;
                late();
            }
            if (failed is { } error)
            {
                failed = null;
                error.Throw();
            }
        }

        static void Run()
        {
            while (true)
            {
                go.Wait();
                try
                {
                    running();
                }
                catch (Exception e)
                {
                    failed = ExceptionDispatchInfo.Capture(e);
                }
                running = null;
                done.Set();
            }
        }
    }
}
