using System;
using System.IO;
using System.Windows.Forms;

namespace FPVTracksideCore
{
    /// <summary>
    /// The main class.
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        //[STAThread]
        [MTAThread]
        static void Main()
        {
#if !DEBUG
            try
#endif
            {
                // Actions run through PlatformTools.Invoke go on the window's message loop, so an exception in one
                // surfaces here, in WinForms' own dialog, and not through AppDomain.UnhandledException: logged, or it's
                // lost.
                Application.ThreadException += (sender, e) =>
                {
                    Tools.Logger.CrashLogger?.Log(e.Exception);
                    Tools.Logger.UI.LogException("Unhandled exception on the window thread", e.Exception);
                    MessageBox.Show(e.Exception.ToString(), e.Exception.GetType().Name);
                };

                using (var game = new FPVTracksideCoreGame())
                {
                    game.Run();
                }
            }
#if !DEBUG
            catch (Exception ex)
            {
                Tools.Logger.UI.LogException(ex, ex);
                MessageBox.Show(ex.ToString(), ex.GetType().Name);
            }
#endif
        }
    }
}


