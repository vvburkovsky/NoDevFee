using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using WinDivert;

namespace NoDevFee
{
    internal class Program
    {
        private static string _strOurWallet = "0x1234567890123456789012345678901234567890"; //Default wallet with out argument
        private static readonly string _poolAddress = "eu1.ethermine.org";
        private static readonly string _poolPort = "4444";

        private static byte[] _byteOurWallet = Encoding.ASCII.GetBytes(_strOurWallet);
        private static int _counter = 0;
        private static IntPtr _divertHandle;
        private static bool _running = true;

        private static void Main(string[] args)
        {
            Console.WriteLine("Init..");

            if (args.Length >= 1)
            {
                if (args[0].Length < 42 || args[0].Length > 42)
                {
                    Console.WriteLine(@"ERROR: Invalid ETH Wallet, should be 42 chars long.");
                    Console.Read();
                    
                    return;
                }

                _strOurWallet = args[0];
                _byteOurWallet = Encoding.ASCII.GetBytes(_strOurWallet);
            }
            else
            {
                Console.WriteLine(@"INFO: No wallet argument was found, using the default wallet.");
            }

            Console.WriteLine($"Current Wallet: {_strOurWallet}\n");
            InstallWinDivert();

            var hosts = Dns.GetHostAddresses(_poolAddress);
            Console.WriteLine($"ip {hosts[0]}, pool: {_poolAddress}, port: {_poolPort}");

            // Create filter
            var filter = $"!loopback and outbound && ip && tcp && tcp.PayloadLength > 0 && ip.DstAddr == {hosts[0]} && tcp.DstPort == {_poolPort}";

            // Check filter 
            var ret = WinDivertNative.WinDivertHelperCompileFilter(filter, WinDivertNative.WinDivertLayer.Network, IntPtr.Zero, 0, out IntPtr errStrPtr, out uint errPos);

            if (!ret)
            {
                var errStr = Marshal.PtrToStringAnsi(errStrPtr);

                throw new Exception($"Filter string is invalid at position {errPos}\n{errStr}");
            }

            // Open new handle
            _divertHandle = WinDivertNative.WinDivertOpen(filter, WinDivertNative.WinDivertLayer.Network, 0, 0);

            // Check handle is null
            if (_divertHandle == IntPtr.Zero)
            {
                return;
            }

            Console.CancelKeyPress += delegate { _running = false; };
            Console.WriteLine(@"Listening..");
            Divert();
            WinDivertNative.WinDivertClose(_divertHandle);
        }

        private unsafe static void Divert()
        {
            // Allocate buffer
            var buffer = new byte[4096];

            try
            {
                fixed (byte* p = buffer)
                {
                    var ptr = new IntPtr(p);

                    while (_running)
                    {
                        // Receive data
                        WinDivertNative.WinDivertRecv(_divertHandle, ptr, (uint)buffer.Length, out uint readLen, out WinDivertNative.Address addr);

                        // Process captured packet
                        var changed = ProcessPacket(buffer, readLen);

                        // Recalculate checksum
                        if (changed)
                        {
                            WinDivertNative.WinDivertHelperCalcChecksums(ptr, readLen, 0);
                        }

                        WinDivertNative.WinDivertSend(_divertHandle, ptr, readLen, out var pSendLen, ref addr);
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
                Console.ReadLine();

                return;
            }
        }

        // Returns true if the packet was altered with
        private static bool ProcessPacket(byte[] buffer, uint length)
        {
            var content = Encoding.ASCII.GetString(buffer, 0, (int)length);
            var pos = 0;
            string dwallet;

            if (content.Contains("eth_submitLogin"))
            {
                pos = 120;
            }

            if (content.Contains("eth_login"))
            {
                pos = 96;
            }

            if (pos != 0 && !content.Contains(_strOurWallet) && !(dwallet = Encoding.UTF8.GetString(buffer, pos, 42)).Contains("params"))
            {
                Buffer.BlockCopy(_byteOurWallet, 0, buffer, pos, 42);
                Console.WriteLine($"-> Diverting Phoenix Minner DevFee {++_counter}: ({dwallet}) changed to {_strOurWallet}\n{DateTime.Now}\n");
                
                return true;
            }

            return false;
        }

        private static void InstallWinDivert()
        {
            var path = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
            var version = "2.2.0";
            var arch = IntPtr.Size == 8 ? "x64" : "x86";
            var driverPath = Path.Combine(path, $"WinDivert-{version}-A\\{arch}");

            // Download driver if not already there
            if (!File.Exists($"{driverPath}\\WinDivert.dll"))
            {
                Console.WriteLine(@"Installing driver..");

                var zipFile = Path.Combine(path, "windivert.zip");

                using (var client = new WebClient())
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    client.DownloadFile($"https://github.com/basil00/Divert/releases/download/v{version}/WinDivert-{version}-A.zip", zipFile);
                }

                ZipFile.ExtractToDirectory(zipFile, path);
            }

            // Patch PATH env
            Environment.SetEnvironmentVariable("PATH", $@"{Environment.GetEnvironmentVariable("PATH") ?? string.Empty}{Path.PathSeparator}{driverPath}");
        }
    }
}