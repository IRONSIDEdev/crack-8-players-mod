using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Crack8PInstaller
{
    internal static class Program
    {
        public const string ModVersion = "1.1.0";
        public const string Credit = "Mod Desenvolvido por Maçonicos";
        public const string DefaultGameDir = @"C:\Program Files (x86)\Steam\steamapps\common\CRACK";

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Relançado como administrador: --dir "<pasta>" --install|--uninstall
            string dir = null, action = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--dir" && i + 1 < args.Length) dir = args[++i];
                else if (args[i] == "--install" || args[i] == "--uninstall") action = args[i];
            }
            Application.Run(new MainForm(dir, action));
        }
    }

    internal static class Steam
    {
        /// <summary>Pasta pré-definida primeiro; se o jogo não estiver lá, procura nas bibliotecas da Steam.</summary>
        public static string FindGameDir()
        {
            if (IsGameDir(Program.DefaultGameDir)) return Program.DefaultGameDir;
            foreach (var lib in Libraries())
            {
                var candidate = Path.Combine(lib, "steamapps", "common", "CRACK");
                if (IsGameDir(candidate)) return candidate;
            }
            return Program.DefaultGameDir;
        }

        public static bool IsGameDir(string dir) =>
            !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "CRACK.exe"))
                                            && Directory.Exists(Path.Combine(dir, "CRACK_Data"));

        private static IEnumerable<string> Libraries()
        {
            var roots = new List<string>();
            void Add(string p) { if (!string.IsNullOrWhiteSpace(p)) roots.Add(p.Replace('/', '\\')); }
            try { Add(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string); } catch { }
            try { Add(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string); } catch { }
            try { Add(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null) as string); } catch { }

            var result = new List<string>();
            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                result.Add(root);
                var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                try
                {
                    if (!File.Exists(vdf)) continue;
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        result.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
                }
                catch { }
            }
            foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
            {
                result.Add(Path.Combine(d.Name, "SteamLibrary"));
                result.Add(Path.Combine(d.Name, "Steam"));
            }
            return result.Distinct(StringComparer.OrdinalIgnoreCase);
        }
    }

    internal static class ModFiles
    {
        public static readonly string[] RootFiles =
            { "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt", "LEIA-ME.txt", "DESINSTALAR-MOD.bat" };

        public static string PluginDll(string gameDir) => Path.Combine(gameDir, "BepInEx", "plugins", "Crack8P", "Crack8P.dll");

        public static string InstalledVersion(string gameDir)
        {
            var dll = PluginDll(gameDir);
            if (!File.Exists(dll)) return null;
            try { return FileVersionInfo.GetVersionInfo(dll).FileVersion ?? "?"; } catch { return "?"; }
        }

        public static bool GameRunning() => Process.GetProcessesByName("CRACK").Length > 0;

        public static void Install(string gameDir, Action<string> log)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                string root = Path.GetFullPath(gameDir).TrimEnd('\\') + "\\";
                foreach (var entry in zip.Entries)
                {
                    string dest = Path.GetFullPath(Path.Combine(gameDir, entry.FullName.Replace('/', '\\')));
                    if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // segurança

                    if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(dest); continue; }

                    // Não sobrescreve configurações já existentes do jogador.
                    bool isConfig = entry.FullName.StartsWith("BepInEx/config/", StringComparison.OrdinalIgnoreCase);
                    if (isConfig && File.Exists(dest)) { log($"mantido: {entry.FullName}"); continue; }

                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    entry.ExtractToFile(dest, true);
                    log($"instalado: {entry.FullName}");
                }
            }
        }

        public static bool Uninstall(string gameDir, Action<string> log)
        {
            bool ok = true;
            foreach (var f in RootFiles)
            {
                var p = Path.Combine(gameDir, f);
                if (!File.Exists(p)) continue;
                try { File.Delete(p); log($"apagado: {f}"); }
                catch (Exception e) when (!(e is UnauthorizedAccessException)) { ok = false; log($"FALHOU: {f} ({e.Message})"); }
            }
            var bep = Path.Combine(gameDir, "BepInEx");
            if (Directory.Exists(bep))
            {
                try { Directory.Delete(bep, true); log("apagado: pasta BepInEx"); }
                catch (Exception e) when (!(e is UnauthorizedAccessException)) { ok = false; log($"FALHOU: pasta BepInEx ({e.Message})"); }
            }
            return ok;
        }
    }

    internal class MainForm : Form
    {
        private readonly TextBox _path = new TextBox();
        private readonly Label _status = new Label();
        private readonly Button _install = new Button();
        private readonly Button _uninstall = new Button();
        private readonly Button _open = new Button();
        private readonly TextBox _log = new TextBox();
        private readonly string _autoAction;

        public MainForm(string dir, string autoAction)
        {
            _autoAction = autoAction;
            Text = $"Instalador - CRACK Mod 8 Jogadores v{Program.ModVersion}";
            Font = new Font("Segoe UI", 9.5f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(640, 470);
            BackColor = Color.White;
            AutoScaleMode = AutoScaleMode.Dpi;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var title = new Label
            {
                Text = "CRACK — Mod 8 Jogadores",
                Font = new Font("Segoe UI", 15f, FontStyle.Bold),
                AutoSize = true, Location = new Point(150, 34)
            };
            var logo = new PictureBox
            {
                Image = LoadLogo(), SizeMode = PictureBoxSizeMode.Zoom,
                Location = new Point(16, 12), Size = new Size(120, 117)
            };
            var subtitle = new Label
            {
                Text = "Aumenta o limite do lobby de 4 para 8 jogadores. Todos do grupo precisam instalar.",
                AutoSize = true, MaximumSize = new Size(470, 0), Location = new Point(152, 70), ForeColor = SystemColors.GrayText
            };

            var lblPath = new Label { Text = "Pasta do jogo:", AutoSize = true, Location = new Point(18, 146) };
            _path.SetBounds(18, 168, 510, 26);
            _path.Text = string.IsNullOrWhiteSpace(dir) ? Steam.FindGameDir() : dir;
            _path.TextChanged += (s, e) => RefreshStatus();

            var browse = new Button { Text = "Procurar…" };
            browse.SetBounds(536, 166, 88, 30);
            browse.Click += (s, e) => Browse();

            var reset = new LinkLabel { Text = "Usar pasta padrão da Steam", AutoSize = true, Location = new Point(18, 198) };
            reset.LinkClicked += (s, e) => _path.Text = Steam.FindGameDir();

            _status.SetBounds(18, 224, 606, 22);
            _status.Font = new Font(Font, FontStyle.Bold);

            _install.Text = "Instalar / Atualizar";
            _install.SetBounds(18, 254, 170, 36);
            _install.Click += (s, e) => DoInstall();

            _uninstall.Text = "Desinstalar";
            _uninstall.SetBounds(196, 254, 120, 36);
            _uninstall.Click += (s, e) => DoUninstall();

            _open.Text = "Abrir pasta";
            _open.SetBounds(324, 254, 110, 36);
            _open.Click += (s, e) => { if (Directory.Exists(_path.Text)) Process.Start("explorer.exe", $"\"{_path.Text}\""); };

            _log.SetBounds(18, 300, 606, 130);
            _log.Multiline = true; _log.ReadOnly = true; _log.ScrollBars = ScrollBars.Vertical;
            _log.BackColor = SystemColors.Window;

            var credit = new Label
            {
                Text = Program.Credit,
                AutoSize = true, Location = new Point(18, 442), ForeColor = SystemColors.GrayText
            };

            Controls.AddRange(new Control[] { logo, title, subtitle, lblPath, _path, browse, reset, _status, _install, _uninstall, _open, _log, credit });
            Shown += (s, e) =>
            {
                RefreshStatus();
                if (_autoAction == "--install") DoInstall();
                else if (_autoAction == "--uninstall") DoUninstall(confirm: false);
            };
        }

        private static Image LoadLogo()
        {
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png"))
                    return s == null ? null : new Bitmap(Image.FromStream(s));
            }
            catch { return null; }
        }

        private string GameDir => _path.Text.Trim().Trim('"');

        private void Log(string line)
        {
            _log.AppendText(line + Environment.NewLine);
        }

        private void RefreshStatus()
        {
            if (!Steam.IsGameDir(GameDir))
            {
                _status.ForeColor = Color.Firebrick;
                _status.Text = "CRACK.exe não encontrado nesta pasta. Use \"Procurar…\".";
                _install.Enabled = _uninstall.Enabled = false;
                return;
            }
            var ver = ModFiles.InstalledVersion(GameDir);
            _install.Enabled = true;
            _uninstall.Enabled = ver != null || File.Exists(Path.Combine(GameDir, "winhttp.dll"));
            if (ver == null)
            {
                _status.ForeColor = SystemColors.ControlText;
                _status.Text = "Jogo encontrado. Mod não instalado.";
            }
            else
            {
                _status.ForeColor = Color.SeaGreen;
                _status.Text = $"Mod instalado (versão {ver}). Este instalador tem a versão {Program.ModVersion}.";
            }
        }

        private void Browse()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Selecione a pasta do CRACK (onde fica o CRACK.exe)";
                dlg.ShowNewFolderButton = false;
                if (Directory.Exists(GameDir)) dlg.SelectedPath = GameDir;
                if (dlg.ShowDialog(this) == DialogResult.OK) _path.Text = dlg.SelectedPath;
            }
        }

        private bool CheckGameClosed()
        {
            while (ModFiles.GameRunning())
            {
                if (MessageBox.Show(this, "O CRACK está aberto. Feche o jogo e clique em Repetir.", "Jogo aberto",
                        MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning) == DialogResult.Cancel)
                    return false;
            }
            return true;
        }

        private void DoInstall()
        {
            if (!Steam.IsGameDir(GameDir) || !CheckGameClosed()) return;
            _log.Clear();
            Log($"Instalando em: {GameDir}");
            try
            {
                ModFiles.Install(GameDir, Log);
                Log("");
                Log("Pronto! Abra o jogo: a versão do mod aparece na tela inicial.");
                RefreshStatus();
                MessageBox.Show(this, "Mod instalado com sucesso!\n\nLembre: todos do grupo precisam ter o mod.",
                    "Instalado", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (UnauthorizedAccessException)
            {
                RelaunchAsAdmin("--install");
            }
            catch (Exception e)
            {
                Log("ERRO: " + e.Message);
                MessageBox.Show(this, "Falha na instalação:\n" + e.Message, "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void DoUninstall(bool confirm = true)
        {
            if (!Steam.IsGameDir(GameDir) || !CheckGameClosed()) return;
            if (confirm && MessageBox.Show(this,
                    "Remover o mod e o BepInEx desta pasta?\n\nOs arquivos originais do jogo não serão tocados.",
                    "Desinstalar", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            _log.Clear();
            Log($"Desinstalando de: {GameDir}");
            try
            {
                bool ok = ModFiles.Uninstall(GameDir, Log);
                RefreshStatus();
                Log(ok ? "Mod removido. O jogo voltou ao normal (4 jogadores)." : "Alguns itens não puderam ser apagados.");
                if (ok) MessageBox.Show(this, "Mod removido.", "Desinstalado", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (UnauthorizedAccessException)
            {
                RelaunchAsAdmin("--uninstall");
            }
        }

        private void RelaunchAsAdmin(string action)
        {
            Log("Sem permissão para gravar nesta pasta. Pedindo permissão de administrador…");
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Application.ExecutablePath,
                    Arguments = $"--dir \"{GameDir}\" {action}",
                    Verb = "runas",
                    UseShellExecute = true
                });
                Close();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Log("Permissão de administrador negada. Nada foi alterado.");
            }
        }
    }
}
