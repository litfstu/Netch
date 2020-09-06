﻿using System;
using System.IO;
using System.Threading.Tasks;
using Netch.Models;
using Netch.Utils;

namespace Netch.Controllers
{
    public static class MainController
    {
        public static EncryptedProxy EncryptedProxyController { get; private set; }
        public static ModeController ModeController { get; private set; }


        public static Server SavedServer;
        public static Mode SavedMode;

        public static bool IsSocks5Server => SavedServer.Type == "Socks5";
        public static string LocalAddress;
        public static int RedirectorTcpPort;
        public static int HttpPort;
        public static int Socks5Port;

        public static bool NttTested;

        private static readonly NTTController NTTController = new NTTController();

        /// <summary>
        ///     启动
        /// </summary>
        /// <param name="server">服务器</param>
        /// <param name="mode">模式</param>
        /// <returns>是否启动成功</returns>
        public static async Task<bool> Start(Server server, Mode mode)
        {
            Logging.Info($"启动主控制器: {server.Type} [{mode.Type}]{mode.Remark}");

            #region Record Settings

            HttpPort = Global.Settings.HTTPLocalPort;
            Socks5Port = server.Type != "Socks5" ? Global.Settings.Socks5LocalPort : server.Port;
            RedirectorTcpPort = Global.Settings.RedirectorTCPPort;
            LocalAddress = server.Type != "Socks5" ? Global.Settings.LocalAddress : "127.0.0.1";
            SavedServer = server;
            SavedMode = mode;

            #endregion

            NativeMethods.FlushDNSResolverCache();
            _ = Task.Run(Firewall.AddNetchFwRules);

            bool result;
            if (IsSocks5Server)
            {
                result = mode.Type != 4;
            }
            else
            {
                EncryptedProxyController = server.Type switch
                {
                    "SS" => new SSController(),
                    "SSR" => new SSRController(),
                    "VMess" => new VMessController(),
                    "Trojan" => new TrojanController(),
                    _ => EncryptedProxyController
                };

                Utils.Utils.KillProcessByName(EncryptedProxyController.MainFile);

                #region 检查端口是否被占用

                static bool PortCheckAndShowMessageBox(int port, string portName, PortType portType = PortType.Both)
                {
                    if (!PortHelper.PortInUse(port, portType)) return false;
                    MessageBoxX.Show(i18N.TranslateFormat("The {0} port is in use.", portName));
                    return true;
                }

                var portNotAvailable = false;
                if (!IsSocks5Server)
                {
                    portNotAvailable |= PortCheckAndShowMessageBox(Socks5Port, "Socks5");
                }

                switch (SavedMode.Type)
                {
                    case 0:
                        portNotAvailable |= PortCheckAndShowMessageBox(RedirectorTcpPort, "Redirector TCP");
                        break;
                    case 3:
                    case 5:
                        portNotAvailable |= PortCheckAndShowMessageBox(HttpPort, "HTTP");
                        break;
                }

                if (portNotAvailable)
                {
                    Logging.Error("主控制器启动失败: 端口被占用");
                    return false;
                }

                #endregion

                Global.MainForm.StatusText(i18N.Translate("Starting ", EncryptedProxyController.Name));
                try
                {
                    result = await Task.Run(() => EncryptedProxyController.Start(server, mode));
                }
                catch (Exception e)
                {
                    Logging.Error("加密代理启动失败，未处理异常: " + e);
                    result = false;
                }
            }

            if (result)
            {
                // 加密代理成功启动

                switch (mode.Type)
                {
                    case 0: // 进程代理模式
                        ModeController = new NFController();
                        break;
                    case 1: // TUN/TAP 黑名单代理模式
                    case 2: // TUN/TAP 白名单代理模式
                        ModeController = new TUNTAPController();
                        break;
                    case 3:
                    case 5:
                        ModeController = new HTTPController();
                        break;
                    case 4: // Socks5 代理模式，不需要启动额外的Server
                        result = true;
                        break;
                }

                if (ModeController != null)
                {
                    Global.MainForm.StatusText(i18N.Translate("Starting ", ModeController.Name));
                    try
                    {
                        result = await Task.Run(() => ModeController.Start(server, mode));
                    }
                    catch (Exception e)
                    {
                        if (e is DllNotFoundException || e is FileNotFoundException)
                            MessageBoxX.Show(e.Message + "\n\n" + i18N.Translate("Missing File or runtime components"), owner: Global.MainForm);
                        else
                            Logging.Error("模式启动失败，未处理异常: " + e);
                        result = false;
                    }
                }

                if (result)
                {
                    // 成功启动

                    if (!IsSocks5Server)
                        PortHelper.UsingPorts.Add(Socks5Port);

                    switch (mode.Type) // 记录使用端口
                    {
                        case 0:
                            PortHelper.UsingPorts.Add(RedirectorTcpPort);
                            break;
                        case 3:
                        case 5:
                            PortHelper.UsingPorts.Add(HttpPort);
                            break;
                    }

                    switch (mode.Type)
                    {
                        case 0:
                        case 1:
                        case 2:
                            NatTest();
                            break;
                    }
                }
            }

            if (!result)
            {
                Logging.Error("主控制器启动失败");
                try
                {
                    await Stop();
                }
                catch
                {
                    // ignored
                }
            }

            return result;
        }

        /// <summary>
        ///     停止
        /// </summary>
        public static async Task Stop()
        {
            HttpPort = Socks5Port = RedirectorTcpPort = 0;
            LocalAddress = null;
            SavedMode = null;
            SavedServer = null;
            PortHelper.UsingPorts.Clear();

            var tasks = new[]
            {
                Task.Run(() => EncryptedProxyController?.Stop()),
                Task.Run(() => ModeController?.Stop()),
                Task.Run(() => NTTController.Stop())
            };
            await Task.WhenAll(tasks);
        }

        /// <summary>
        ///     测试 NAT
        /// </summary>
        public static void NatTest()
        {
            NttTested = false;
            Task.Run(() =>
            {
                Global.MainForm.NatTypeStatusText(i18N.Translate("Starting NatTester"));
                // Thread.Sleep(1000);
                var (nttResult, natType, localEnd, publicEnd) = NTTController.Start();

                if (nttResult)
                {
                    var country = Utils.Utils.GetCityCode(publicEnd);
                    Global.MainForm.NatTypeStatusText(natType, country);
                }
                else
                    Global.MainForm.NatTypeStatusText(natType);

                NttTested = true;
            });
        }
    }
}