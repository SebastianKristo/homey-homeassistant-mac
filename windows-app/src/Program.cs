namespace MacBridge;

static class Program
{
    [STAThread]
    static int Main()
    {
        // Only one MacBridge may run (it owns the port). A newer copy asks the running one to quit
        // first, like the Mac app does, so updating is just "start the new exe".
        using var quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\MacBridge-Quit");
        using var running = new Mutex(false, @"Local\MacBridge-Running");
        if (!Acquire(running, TimeSpan.Zero))
        {
            quitSignal.Set();
            if (!Acquire(running, TimeSpan.FromSeconds(5)))
            {
                MessageBox.Show("Another MacBridge is still running and did not quit.", "MacBridge");
                return 1;
            }
        }

        try
        {
            ApplicationConfiguration.Initialize();
            using var app = new TrayApp();
            var sync = SynchronizationContext.Current!;
            ThreadPool.RegisterWaitForSingleObject(quitSignal, (_, _) => sync.Post(_ => app.ExitThread(), null),
                null, Timeout.Infinite, true);
            Application.Run(app);
            return 0;
        }
        finally
        {
            running.ReleaseMutex();
        }
    }

    private static bool Acquire(Mutex mutex, TimeSpan timeout)
    {
        try
        {
            return mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            return true; // the previous copy crashed
        }
    }
}
