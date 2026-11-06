using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DdalggakBackup
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static int Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch (EntryPointNotFoundException) { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                MessageBox.Show("화면 처리 중 오류가 발생했습니다.\r\n" + e.Exception.Message,
                    "딸깍 백업", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            if (args.Length > 0)
            {
                if (args.Length != 2 || args[0] != "--smoke-test") return 2;
                return SmokeTest(args[1]);
            }
            Application.Run(new MainForm());
            return 0;
        }

        // Explicit local fixture only. This route does not load or save real settings,
        // executes its explicit test backup and isolated overwrite fixture only beneath that fixture,
        // and cannot probe the deployed UNC defaults.
        private static int SmokeTest(string fixtureRoot)
        {
            try
            {
                string root = PathSafety.Canonicalize(Path.GetFullPath(fixtureRoot));
                if (root.StartsWith(@"\\", StringComparison.Ordinal) || !Directory.Exists(root)) return 2;
                string source = Path.Combine(root, "source");
                string backup = Path.Combine(root, "backup");
                if (!Directory.Exists(source) || !Directory.Exists(backup)) return 2;
                AppSettings settings = SettingsStore.Defaults();
                foreach (MachineProfile profile in settings.Profiles)
                {
                    profile.SourceRoot = source;
                    profile.BackupRoot = backup;
                }
                settings.SelectedProfile = 0;
                settings.LastName = "과검제어전";
                int code = 0;
                using (MainForm form = new MainForm(settings, false, null))
                {
                    form.Shown += async delegate
                    {
                        try
                        {
                            await form.VerifyAndCaptureSmokeAsync(root);
                        }
                        catch (Exception error)
                        {
                            code = 1;
                            File.WriteAllText(Path.Combine(root, "ui-smoke-error.txt"), error.ToString());
                        }
                        finally { form.Close(); }
                    };
                    Application.Run(form);
                }
                return code;
            }
            catch { return 1; }
        }
    }
}
