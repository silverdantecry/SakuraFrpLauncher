﻿using SakuraFrpService.Manager;
using SakuraLibrary;
using System;
using System.ComponentModel;
using System.Configuration.Install;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using System.Windows.Forms;

namespace SakuraFrpService
{
    static class Program
    {
        public static Mutex AppMutex = null;

        private static string InstallService()
        {
            // Install service
            var dir = new DirectoryInfo(Path.GetDirectoryName(Utils.ExecutablePath));

            var acl = dir.GetAccessControl(AccessControlSections.Access);
            acl.SetAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null), FileSystemRights.FullControl, InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));

            dir.SetAccessControl(acl);

            ManagedInstallerClass.InstallHelper(new string[] { Utils.ExecutablePath });

            // Set permission
            var sc = ServiceController.GetServices().FirstOrDefault(s => s.ServiceName == Consts.ServiceName);
            if (sc == null)
            {
                return "Service installation failure";
            }

            var buffer = new byte[0];
            if (!NTAPI.QueryServiceObjectSecurity(sc.ServiceHandle, SecurityInfos.DiscretionaryAcl, buffer, 0, out uint size))
            {
                int err = Marshal.GetLastWin32Error();
                if (err != 122 && err != 0) // ERROR_INSUFFICIENT_BUFFER
                {
                    return "QueryServiceObjectSecurity[1] error: " + err;
                }
                buffer = new byte[size];
                if (!NTAPI.QueryServiceObjectSecurity(sc.ServiceHandle, SecurityInfos.DiscretionaryAcl, buffer, size, out size))
                {
                    return "QueryServiceObjectSecurity[2] error: " + Marshal.GetLastWin32Error();
                }
            }

            var rsd = new RawSecurityDescriptor(buffer, 0);

            var dacl = new DiscretionaryAcl(false, false, rsd.DiscretionaryAcl);
            dacl.SetAccess(AccessControlType.Allow, new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), (int)(ServiceAccessRights.SERVICE_QUERY_STATUS | ServiceAccessRights.SERVICE_START | ServiceAccessRights.SERVICE_STOP | ServiceAccessRights.SERVICE_INTERROGATE), InheritanceFlags.None, PropagationFlags.None);

            buffer = new byte[dacl.BinaryLength];
            dacl.GetBinaryForm(buffer, 0);

            rsd.DiscretionaryAcl = new RawAcl(buffer, 0);

            buffer = new byte[rsd.BinaryLength];
            rsd.GetBinaryForm(buffer, 0);

            if (!NTAPI.SetServiceObjectSecurity(sc.ServiceHandle, SecurityInfos.DiscretionaryAcl, buffer))
            {
                return "SetServiceObjectSecurity error: " + Marshal.GetLastWin32Error();
            }
            return null;
        }

        private static void UninstallService()
        {
            ManagedInstallerClass.InstallHelper(new string[] { "/u", Utils.ExecutablePath });
        }

        private static T ResetACL<T>(T acl) where T : FileSystemSecurity
        {
            acl.SetAccessRuleProtection(false, false);
            foreach (FileSystemAccessRule r in acl.GetAccessRules(true, false, typeof(NTAccount)))
            {
                acl.RemoveAccessRule(r);
            }
            return acl;
        }

        private static void ResetACLRecursive(DirectoryInfo dir, IdentityReference owner, bool root = true)
        {
            if (!dir.Exists)
            {
                return;
            }
            var acl = ResetACL(dir.GetAccessControl());
            if (root)
            {
                acl.SetAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
            }
            dir.SetAccessControl(acl);

            foreach (var file in dir.GetFiles())
            {
                file.SetAccessControl(ResetACL(file.GetAccessControl()));
            }
            foreach (var d in dir.GetDirectories())
            {
                ResetACLRecursive(d, owner, false);
            }
        }

        /// <summary>
        /// 应用程序的主入口点。
        /// </summary>
        static int Main(string[] argv)
        {
            Environment.CurrentDirectory = Path.GetDirectoryName(Utils.ExecutablePath);

            Utils.VerifySignature(Utils.LibraryPath, Utils.ExecutablePath, Path.GetFullPath(TunnelManager.FrpcExecutable));
            Utils.ValidateSettings();

            if (Path.GetFileName(Utils.ExecutablePath) != Consts.ServiceExecutable)
            {
                if (Environment.UserInteractive)
                {
                    MessageBox.Show("Do NOT rename SakuraFrpService.exe\n请不要重命名 SakuraFrpService.exe", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return 1;
            }
            if (argv.Length != 0)
            {
                switch (argv[0])
                {
                case "--install":
                    try
                    {
                        var result = InstallService();
                        if (result != null)
                        {
                            MessageBox.Show("无法设置服务操作权限, 启动器可能无法正常运行\n请截图此错误并联系管理员:\n" + result, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return 3;
                        }
                    }
                    catch (Exception e) when (e.InnerException is Win32Exception w32 && w32.NativeErrorCode == 1073) // ERROR_SERVICE_EXISTS
                    {
                        // Ensure the service was installed correctly
                        using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Service WHERE Name = '" + Consts.ServiceName + "'"))
                        using (var collection = searcher.Get())
                        {
                            var service = collection.OfType<ManagementObject>().FirstOrDefault();
                            if (service != null)
                            {
                                var oldPath = Path.GetFullPath((service.GetPropertyValue("PathName") as string).Trim('"'));
                                var newPath = Path.GetFullPath(Consts.ServiceExecutable);
                                if (oldPath != newPath)
                                {
                                    // Delete bad service and reinstall
                                    try
                                    {
                                        UninstallService();
                                        InstallService();
                                    }
                                    catch (Exception e1)
                                    {
                                        MessageBox.Show(e1.ToString(), "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                        return 2;
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        MessageBox.Show(e.ToString(), "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return 2;
                    }
                    try
                    {
                        ResetACLRecursive(new DirectoryInfo(Consts.WorkingDirectory), new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null));
                    }
                    catch (Exception e)
                    {
                        MessageBox.Show("无法设置工作路径 ACL, 启动器可能无法正常运行\n请截图此错误并联系管理员:\n" + e.ToString(), "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return 4;
                    }
                    return 0;
                case "--uninstall":
                    try
                    {
                        UninstallService();
                    }
                    catch (Exception e) when (e.InnerException is Win32Exception w32 && w32.NativeErrorCode == 1060) // ERROR_SERVICE_DOES_NOT_EXIST
                    {
                        // Can be safely ignored
                    }
                    catch (Exception e)
                    {
                        MessageBox.Show(e.ToString(), "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return 2;
                    }
                    try
                    {
                        ResetACLRecursive(new DirectoryInfo(Consts.WorkingDirectory), WindowsIdentity.GetCurrent().User);
                    }
                    catch (Exception e)
                    {
                        MessageBox.Show("无法设置工作路径 ACL, 启动器可能无法正常运行\n请截图此错误并联系管理员:\n" + e.ToString(), "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return 4;
                    }
                    return 0;
                case "--daemon":
                    var args = new string[argv.Length - 1];
                    Array.Copy(argv, 1, args, 0, args.Length);

                    var main = new MainService(true);
                    main.DaemonRun(args);
                    return main.ExitCode;
                case "--update":
                    if (argv.Length < 2)
                    {
                        MessageBox.Show("参数错误", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return 1;
                    }
                    File.WriteAllBytes(Consts.UpdaterExecutable, Properties.Resources.Updater);
                    Process.Start(Consts.UpdaterExecutable, '"' + argv[1] + '"' + (argv.Length >= 3 ? ' ' + argv[2] : ""));
                    Environment.Exit(0);
                    return 0;
                }
            }
            if (Environment.UserInteractive)
            {
                MessageBox.Show("You can't start the service directly.\nTo run as daemon, pass --daemon as the first parameter.\nKeep in mind that action above should be done by SakuraFrpLauncher automatically, not by user.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return -1;
            }

            AppMutex = new Mutex(true, "SakuraFrpService_" + Utils.InstallationHash, out bool created);
            if (!created)
            {
                if (Environment.UserInteractive)
                {
                    MessageBox.Show("请勿重复开启守护进程", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return -1;
            }

            ServiceBase[] ServicesToRun;
            ServicesToRun = new ServiceBase[]
            {
                new MainService(false)
            };
            ServiceBase.Run(ServicesToRun);
            return 0;
        }
    }
}
