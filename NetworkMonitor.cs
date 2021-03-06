﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.DirectoryServices;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace LanMonitor
{
    public class CustomINotifyPropertyChanged : INotifyPropertyChanged
    {
        /// <summary>
        /// 属性变化时的事件处理
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// 通知UI更新数据的方法
        /// </summary>
        /// <typeparam name="T">泛型参数</typeparam>
        /// <param name="propertyExpression">待更新的数据项</param>
        protected void Notify<T>(Expression<Func<T>> propertyExpression)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs((propertyExpression.Body as MemberExpression).Member.Name));
        }
    }
    
    public class NetworkManager : CustomINotifyPropertyChanged, IDisposable
    {
        public ObservableCollection<NetworkModelView> NetworkCollection { get; set; }
        public ObservableCollection<LANComputerModelView> ComputerCollection { get; set; }

        private readonly NetworkMonitor networkMoniter;
        private readonly LocalNetworkManager lanMonitor;

        public string GlobalUploadSpeed { get; set; }
        public string GlobalDownloadSpeed { get; set; }

        public PointCollection GraphPointCollection
        {
            get
            {
                PointCollection collection = new PointCollection
                {
                    new Point(0, 20)
                };
                int x = 0;
                long y = 0;
                foreach (long speed in speedQueue)
                {
                    y = (1000000 - speed) * 20 / 1000000;
                    collection.Add(new Point(x, y));
                    x += 1;
                }
                collection.Add(new Point(x, 20));
                return collection;
            }
        }

        private readonly Queue<long> speedQueue;

        public NetworkManager()
        {
            speedQueue = new Queue<long>();

            NetworkCollection = new ObservableCollection<NetworkModelView>();

            ComputerCollection = new ObservableCollection<LANComputerModelView>();

            networkMoniter = new NetworkMonitor();

            lanMonitor = new LocalNetworkManager();
        }

        public void Start()
        {
            Task.Factory.StartNew(NetworkMonitoring, TaskCreationOptions.LongRunning);
            Task.Factory.StartNew(NetworkAdapterMonitoring, TaskCreationOptions.LongRunning);
            Task.Factory.StartNew(LocalNetworkMonitoring, TaskCreationOptions.LongRunning);
            Task.Factory.StartNew(LocalComputerMonitoring, TaskCreationOptions.LongRunning);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (lanMonitor != null)
                {
                    lanMonitor.Dispose();
                }
            }
        }
        
        private void NetworkAdapterMonitoring()
        {
            while (true)
            {
                networkMoniter.EnumerateNetworkAdapters();
                Thread.Sleep(5000);
            }
        }

        private void LocalComputerMonitoring()
        {
            while (true)
            {
                lanMonitor.ListLANComputers();
                Thread.Sleep(1000);
            }
        }

        private void LocalNetworkMonitoring()
        {
            while (true)
            {
                List<LocalNetworkComputer> computerList = lanMonitor.TestLANComputers();
                {
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        int i = 0;
                        for (; i < computerList.Count; i += 1)
                        {
                            if (ComputerCollection.Count <= i)
                            {
                                ComputerCollection.Add(new LANComputerModelView(computerList[i]));
                            }
                            else
                            {
                                ComputerCollection[i].Resolve(computerList[i]);
                            }
                        }
                        while (ComputerCollection.Count > i)
                        {
                            ComputerCollection.RemoveAt(i);
                        }
                    }));
                }

                Thread.Sleep(1000);
            }
        }
        
        private void NetworkMonitoring()
        {
            while (true)
            {
                List<NetworkAdapter> adapters = networkMoniter.Refresh();

                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    long uploadSpeed = 0;
                    long downloadSpeed = 0;

                    int i = 0;
                    for (; i < adapters.Count; i += 1)
                    {
                        uploadSpeed += adapters[i].UploadSpeed;
                        downloadSpeed += adapters[i].downloadSpeed;

                        if (NetworkCollection.Count <= i)
                        {
                            NetworkCollection.Add(new NetworkModelView(adapters[i]));
                        }
                        else
                        {
                            NetworkCollection[i].Resolve(adapters[i]);
                        }
                    }

                    while (NetworkCollection.Count > i)
                    {
                        NetworkCollection.RemoveAt(i);
                    }

                    speedQueue.Enqueue(uploadSpeed + downloadSpeed);

                    while (speedQueue.Count > 200)
                    {
                        speedQueue.Dequeue();
                    }

                    GlobalUploadSpeed = NetworkAdapter.GetSpeedString(uploadSpeed);
                    GlobalDownloadSpeed = NetworkAdapter.GetSpeedString(downloadSpeed);

                    Notify(() => GlobalUploadSpeed);
                    Notify(() => GlobalDownloadSpeed);
                    Notify(() => GraphPointCollection);
                }));

                Thread.Sleep(1000);
            }
        }
    }

    public class LocalNetworkManager : IDisposable
    {
        private readonly List<LocalNetworkComputer> computerList;
        private readonly Ping pinger;

        public LocalNetworkManager()
        {
            computerList = new List<LocalNetworkComputer>();

            pinger = new Ping();

            pinger.PingCompleted += Pinger_PingCompleted;
        }

        private void Pinger_PingCompleted(object sender, PingCompletedEventArgs e)
        {
            LocalNetworkComputer computer = e.UserState as LocalNetworkComputer;

            int result = -1;
            int latency = -1;
            if (e.Cancelled || e.Error != null)
            {
                result = 10;
                latency = 1000;
            }
            else
            {
                result = (int)e.Reply.Status;
                latency = result == 0 ? (int)e.Reply.RoundtripTime : 1000;
            }
            computer.Status = result;
            computer.Latency = latency;
            if (computer.IPAddress == string.Empty)
            {
                computer.IPAddress = e.Reply.Address.ToString();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (pinger != null)
                {
                    pinger.Dispose();
                }
            }
            computerList.Clear();
        }

        public List<LocalNetworkComputer> TestLANComputers()
        {
            computerList.Sort((LocalNetworkComputer x, LocalNetworkComputer y) => x?.Status > y?.Status ? 1 : -1);

            return computerList;
        }

        public void ListLANComputers()
        {
            DirectoryEntry root = new DirectoryEntry("WinNT:");

            foreach (DirectoryEntry computers in root.Children)
            {
                foreach (DirectoryEntry computer in computers.Children)
                {
                    if (computer.Name != "Schema")
                    {
                        LocalNetworkComputer activeComputer = null;
                        foreach (LocalNetworkComputer item in computerList)
                        {
                            if (item.UID == computer.Path)
                            {
                                activeComputer = item;
                                break;
                            }
                        }

                        if (activeComputer == null)
                        {
                            activeComputer = new LocalNetworkComputer(); ;
                            computerList.Add(activeComputer);
                        }

                        activeComputer.Name = computer.Name;
                        activeComputer.UID = computer.Path;
                        activeComputer.Updated = true;
                        
                        string ipAddress = string.Empty;

                        try
                        {
                            IPAddress ipv4 = null;
                            IPAddress ipv6 = null;

                            IPHostEntry ipHost = Dns.GetHostEntry(computer.Name);

                            foreach (IPAddress ip in ipHost.AddressList)
                            {
                                if (ip.AddressFamily == AddressFamily.InterNetwork)
                                {
                                    ipv4 = ip;
                                }
                                else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                                {
                                    ipv6 = ip;
                                }
                            }
                            
                            if (ipv4 != null)
                            {
                                ipAddress = ipv4.ToString();
                                pinger.SendAsync(ipv4, 1000, activeComputer);
                            }
                            else if (ipv6 != null)
                            {
                                ipAddress = ipv6.ToString();
                                pinger.SendAsync(ipv6, 1000, activeComputer);
                            }
                            else
                            {
                                pinger.SendAsync(computer.Name, 1000, activeComputer);
                            }

                        }
                        catch
                        {
                        }
                        activeComputer.IPAddress = ipAddress;
                    }
                }
            }

            computerList.RemoveAll(computer =>
            {
                if (computer.Updated)
                {
                    computer.Updated = false;
                    return false;
                }
                else
                {
                    return true;
                }
            });

            root.Dispose();
        }
    }
    
    public class NetworkMonitor
    {               
        private readonly List<NetworkAdapter> adapterList;

        public NetworkMonitor()
        {
            adapterList = new List<NetworkAdapter>();
        }

        public void EnumerateNetworkAdapters()
        {
            lock (adapterList)
            {
                PerformanceCounterCategory category = new PerformanceCounterCategory("Network Interface");
                string[] interfaceArray = category.GetInstanceNames();

                IEnumerable networkCollection = NetworkInterface
                    .GetAllNetworkInterfaces()
                    .Where(nic => nic.NetworkInterfaceType != NetworkInterfaceType.Loopback && nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                    .OrderBy(nic => nic.OperationalStatus != OperationalStatus.Up)
                    .Select(nic => nic);

                int index = 0;
                foreach (NetworkInterface network in networkCollection)
                {
                    if (adapterList.Count > index && adapterList[index].ID == network.Id)
                    {
                        adapterList[index].SetNetwork(network);
                        index += 1;
                        continue;
                    }
                    string name = network.Name;
                    string description = network.Description;
                    string flag = string.Empty;
                    foreach (string interfaceName in interfaceArray)
                    {
                        if (GetLetter(name) == GetLetter(interfaceName) || GetLetter(description) == GetLetter(interfaceName))
                        {
                            flag = interfaceName;
                            break;
                        }
                    }
                    if (flag != string.Empty)
                    {
                        NetworkAdapter adapter = new NetworkAdapter(network)
                        {
                            downloadCount = new PerformanceCounter("Network Interface", "Bytes Received/sec", flag),
                            uploadCounter = new PerformanceCounter("Network Interface", "Bytes Sent/sec", flag),
                            bandwidthCounter = new PerformanceCounter("Network Interface", "Current Bandwidth", flag)
                        };

                        if (adapterList.Count > index)
                        {
                            adapterList[index] = adapter;
                        }
                        else
                        {
                            adapterList.Add(adapter);
                        }
                    }
                    index += 1;
                }
                while (adapterList.Count > index)
                {
                    adapterList.RemoveAt(index);
                }
            }
        }

        public List<NetworkAdapter> Refresh()
        {
            foreach (NetworkAdapter adapter in adapterList)
            {
                adapter.Refresh();
            }
            return adapterList;
        }

        public void StartMonitoring()
        {
            if (adapterList.Count > 0)
            {
                foreach (NetworkAdapter adapter in adapterList)
                {
                    adapter.Refresh();
                }
            }
        }
        
        public void StopMonitoring()
        {
            if (adapterList.Count > 0)
            {
                foreach (NetworkAdapter adapter in adapterList)
                {
                    adapter.Dispose();
                }
            }
        }

        private static string GetLetter(string input)
        {
            return new string(input.Where(c => char.IsLetterOrDigit(c)).ToArray()).ToLower();
        }
    }

    public class LANComputerModelView : CustomINotifyPropertyChanged
    {
        public string Name { get; set; }
        public string IPAddress { get; set; }
        public string Type { get; set; }
        public string MacAddress { get; set; }
        public string Status { get; set; }
        public string Latency { get; set; }
        public string ToolTip { get; set; }

        public LANComputerModelView(LocalNetworkComputer computer)
        {
            Name = computer.Name;
            Status = computer.Status.ToString();
            IPAddress = computer.IPAddress;

            Latency = computer.Latency == -1 ? "..." : (computer.Latency >= 1000 ? ">1000ms" : computer.Latency.ToString() + "ms");

            ToolTip = string.Format(Application.Current.FindResource("ComputerToolTip").ToString(),
                Environment.NewLine, Name, IPAddress, Latency);
        }

        public void Resolve(LocalNetworkComputer computer)
        {
            string name = computer.Name;
            string status = computer.Status.ToString();
            string ipAddress = computer.IPAddress;

            string latency = computer.Latency == -1 ? "..." : (computer.Latency >= 1000 ? ">1000ms" : computer.Latency.ToString() + "ms");

            string toolTip = string.Format(Application.Current.FindResource("ComputerToolTip").ToString(),
                Environment.NewLine, Name, IPAddress, Latency);

            if (Name != name)
            {
                Name = name;
                Notify(() => Name);
            }
            if (Status != status)
            {
                Status = status;
                Notify(() => Status);
            }
            if (IPAddress != ipAddress)
            {
                IPAddress = ipAddress;
                Notify(() => IPAddress);
            }
            if (Latency != latency)
            {
                Latency = latency;
                Notify(() => Latency);
            }
            if (ToolTip != toolTip)
            {
                ToolTip = toolTip;
                Notify(() => ToolTip);
            }
        }
    }

    public class NetworkModelView : CustomINotifyPropertyChanged
    {
        public string Name { get; set; }
        public string IPAddress { get; set; }
        public string Type { get; set; }
        public string MacAddress { get; set; }
        public string Status { get; set; }
        public string Speed { get; set; }
        public string ToolTip { get; set; }
        public string DownloadSpeed { get; set; }
        public string UploadSpeed { get; set; }

        public NetworkModelView(NetworkAdapter adapter)
        {
            Name = adapter.Name;
            Status = adapter.Status.ToString();
            Type = adapter.Type.ToString();
            DownloadSpeed = adapter.DownloadSpeedString;
            UploadSpeed = adapter.UploadSpeedString;

            ToolTip = string.Format(Application.Current.FindResource("NetworkToolTip").ToString(),
                Environment.NewLine, adapter.Description, adapter.IPAddress, adapter.MACAddress, adapter.MaxSpeed);
        }

        public void Resolve(NetworkAdapter adapter)
        {
            string name = adapter.Name;
            string status = adapter.Status.ToString();
            string type = adapter.Type.ToString();
            string downloadSpeed = adapter.DownloadSpeedString;
            string uploadSpeed = adapter.UploadSpeedString;
            string toolTip = string.Format(Application.Current.FindResource("NetworkToolTip").ToString(),
                Environment.NewLine, adapter.Description, adapter.IPAddress, adapter.MACAddress, adapter.MaxSpeed);

            if (Name != name)
            {
                Name = name;
                Notify(() => Name);
            }
            if (Status != status)
            {
                Status = status;
                Notify(() => Status);
            }
            if (Type != type)
            {
                Type = type;
                Notify(() => type);
            }
            if (DownloadSpeed != downloadSpeed)
            {
                DownloadSpeed = downloadSpeed;
                Notify(() => DownloadSpeed);
            }
            if (UploadSpeed != uploadSpeed)
            {
                UploadSpeed = uploadSpeed;
                Notify(() => UploadSpeed);
            }
            if (ToolTip != toolTip)
            {
                ToolTip = toolTip;
                Notify(() => ToolTip);
            }
        }
    }
}
