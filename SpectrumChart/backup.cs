using Accord.Audio;
using Accord.Audio.Windows;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;
using MsgPack.Serialization;
using System.Data.SQLite;
using System.Globalization;

namespace SpectrumChart
{
    public partial class Form2 : Form
    {
        private ConcurrentQueue<float[]> dataQueue;
        private ConcurrentQueue<float[]> uiQueue;
        private BackgroundWorker backgroundWorkerProcessData;
        private BackgroundWorker backgroundWorkerUpdateUI;
        private BackgroundWorker backgroundWorkerReceiveData;
        private float[] signalBuffer;
        private UdpClient udpClient;
        private IWindow window;
        private const int WindowSize = 512;
        private ACT12x vact;
        private string database = "DataSource = Vibrate.db";

        private Semaphore semaphore;

        private Dictionary<string, ACT12x> deviceList;

        public Form2()
        {
            InitializeComponent();

            deviceList = new Dictionary<string, ACT12x>();

            for (int x = 0; x < checkedListBoxChannel.Items.Count; x++)
            {
                this.checkedListBoxChannel.SetItemChecked(x, false);
            }

            dataQueue = new ConcurrentQueue<float[]>();
            uiQueue = new ConcurrentQueue<float[]>();
            backgroundWorkerProcessData = new BackgroundWorker();
            backgroundWorkerReceiveData = new BackgroundWorker();
            backgroundWorkerUpdateUI = new BackgroundWorker();
            signalBuffer = new float[WindowSize];
            backgroundWorkerProcessData.WorkerSupportsCancellation = true;
            backgroundWorkerProcessData.DoWork += BackgroundWorkerProcessData_DoWork;

            backgroundWorkerUpdateUI.WorkerSupportsCancellation = true;
            backgroundWorkerUpdateUI.DoWork += BackgroundWorkerUpdateUI_DoWork;

            backgroundWorkerReceiveData.WorkerSupportsCancellation = true;
            backgroundWorkerReceiveData.DoWork += BackgroundWorkerReceiveData_DoWork;

            window = RaisedCosineWindow.Hann(WindowSize);

            LoadDevices();

            vact = null;

            //semaphore = new Semaphore(0, 1);
            //Set series chart type
            //chart1.Series["Series1"].ChartType = SeriesChartType.Line;
            //chart2.Series["Series1"].ChartType = SeriesChartType.Line;
            //chart1.Series["Series1"].IsValueShownAsLabel = true;
        }


        private void LoadDevices()
        {
            this.deviceList.Clear();
            using (SQLiteConnection connection = new SQLiteConnection(database))
            {
                connection.Open();
                //string deviceType = "ACT12816";
                string strainStatement = "select RemoteIP,LocalPort,DeviceId,Type,Desc,Path,IsCalculateForce from SensorInfo";
                SQLiteCommand command2 = new SQLiteCommand(strainStatement, connection);
                using (SQLiteDataReader reader = command2.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string remoteIP = reader.GetString(0);
                        int localPort = reader.GetInt32(1);
                        string deviceId = reader.GetString(2);
                        string type = reader.GetString(3);
                        string description = reader.GetString(4);
                        string path = reader.GetString(5);
                        bool isCalculateForce = bool.Parse(reader.GetString(6));
                        //int index = this.dataGridView1.Rows.Add();

                        string[] itemString = { description, type, deviceId, remoteIP, localPort.ToString(), path };
                        ListViewItem item = new ListViewItem(itemString);

                        listView1.Items.Add(item);

                        //config = new SerialACT4238Config(portName, baudrate, timeout, deviceId, type);

                        ACT12x device = null;

                        if (type == "ACT1228CableForce")
                        {
                            device = new ACT1228CableForce(deviceId, remoteIP, localPort, chart1, type, path, this.database, textBoxLog, isCalculateForce);
                        }
                        else if (type == "ACT12816Vibrate")
                        {
                            device = new ACT12816Vibrate(deviceId, remoteIP, localPort, chart1, type, path, this.database, textBoxLog, isCalculateForce);
                        }
                        else if (type == "ACT1228EarthQuake")
                        {
                            device = new ACT1228EarthQuake(deviceId, remoteIP, localPort, chart1, type, path, this.database, textBoxLog, isCalculateForce);
                        }
                        else if (type == "ACT1228Vibrate")
                        {
                            device = new ACT1228Vibrate(deviceId, remoteIP, localPort, chart1, type, path, this.database, textBoxLog, isCalculateForce);
                        }
                        else
                        {

                        }

                        if (device != null)
                        {
                            this.deviceList.Add(deviceId, device);
                        }
                    }
                }
            }
        }

        private void listView1_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (listView1.SelectedIndices != null && listView1.SelectedIndices.Count > 0)
            {
                ListView.SelectedIndexCollection c = listView1.SelectedIndices;
                //textBoxId.Text = listView1.Items[c[0]].SubItems[3].Text;
                //textBoxPort.Text = listView1.Items[c[0]].SubItems[4].Text;

                chart1.Titles[0].Text = listView1.Items[c[0]].SubItems[0].Text;
                //chart1.Titles.Add(listView1.Items[c[0]].SubItems[0].Text);

                string key = listView1.Items[c[0]].SubItems[2].Text;

                if (deviceList.ContainsKey(key))
                {
                    //textBoxLog.AppendText("deviceList contains key:" + key + "\r\n");
                    if (vact == null)
                    {
                        vact = deviceList[key];
                        vact.SetUpdateChart(true);
                        //textBoxLog.AppendText("vact is null\r\n");
                    }
                    else
                    {
                        vact.SetUpdateChart(false);
                        vact = deviceList[key];
                        vact.SetUpdateChart(true);
                        //textBoxLog.AppendText("vact is not null\r\n");
                    }
                }

            }
        }

        private void BackgroundWorkerUpdateUI_DoWork(object sender, DoWorkEventArgs e)
        {
            BackgroundWorker bgWorker = sender as BackgroundWorker;

            int numberOfPointsInChart = 200;
            MessagePackSerializer serializer = MessagePackSerializer.Get<AccWave>();
            //UdpClient udpClient = null;
            //IPAddress remoteIp = IPAddress.Parse("192.168.100.31");
            //try
            //{
            //    udpClient = new UdpClient();
            //    udpClient.Connect("192.168.100.31", 26660);
            //}
            //catch (Exception ex)
            //{
            //    Console.WriteLine(ex.ToString());
            //}
            while (true)
            {
                try
                {
                    float[] data;
                    bool success = uiQueue.TryDequeue(out data);

                    if (success)
                    {
                        //AccWave aw = new AccWave("5600001715001", "016", data);
                        //byte[] result = serializer.PackSingleObject(aw);
                        //udpClient.Send(result, result.Length);

                        chart1.BeginInvoke(new MethodInvoker(() => {

                            for (int i = 0; i < 8; i++)
                            {
                                chart1.Series[i].Points.AddY(data[i]);

                                if (chart1.Series[i].Points.Count > numberOfPointsInChart)
                                {
                                    chart1.Series[i].Points.RemoveAt(0);
                                }
                            }

                            // Adjust Y & X axis scale
                            chart1.ResetAutoValues();

                            // Invalidate chart
                            chart1.Invalidate();

                        }));
                    }
                    else
                    {
                        Thread.Sleep(10);
                    }

                    if (bgWorker.CancellationPending == true)
                    {
                        e.Cancel = true;
                        udpClient.Close();
                        break;
                    }
                }
                catch (Exception ex)
                {
                    //Console.WriteLine(ex.ToString());
                    if (bgWorker.CancellationPending == true)
                    {
                        e.Cancel = true;
                        udpClient.Close();
                        break;
                    }
                }
            }
        }

        private float ExtractChannel(byte higher, byte lower)
        {
            int data = (higher & 0x7f) * 256 + lower;

            if ((higher & 0x80) == 0x80)
            {
                data = -data;
            }

            float channel = (float)(data / 10000.0);
            return channel;
        }

        private void BackgroundWorkerReceiveData_DoWork(object sender, DoWorkEventArgs e)
        {
            BackgroundWorker bgWorker = sender as BackgroundWorker;

            while (true)
            {
                try
                {
                    //IPEndPoint object will allow us to read datagrams sent from any source.
                    IPEndPoint RemoteIpEndPoint = new IPEndPoint(IPAddress.Any, 0);

                    //Blocks until a message returns on this socket from a remote host.
                    Byte[] receiveBytes = udpClient.Receive(ref RemoteIpEndPoint);

                    float[] data = new float[8];

                    for (int i = 0; i < 8; i++)
                    {
                        data[i] = ExtractChannel(receiveBytes[i * 2 + 4], receiveBytes[i * 2 + 5]);
                    }

                    dataQueue.Enqueue(data);
                    uiQueue.Enqueue(data);

                    //DirectoryInfo d = new DirectoryInfo("1");

                    //FileSystemInfo[] infos = d.GetFileSystemInfos();

                    //foreach(FileSystemInfo item in infos)
                    //{
                    //    if(item is DirectoryInfo)
                    //    {

                    //    }
                    //    else
                    //    {
                    //        ParseFile(item.FullName);
                    //    }
                    //}
                    //string dirName = @"D:\监测数据\瑞丽江\20Hz\";
                    //ProcessFiles(dirName);

                    ////ParseFile("1.txt");
                    //AppendLog("本轮测试完成");

                    if (bgWorker.CancellationPending == true)
                    {
                        e.Cancel = true;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    //Console.WriteLine(ex.ToString());ss
                    if (bgWorker.CancellationPending == true)
                    {
                        e.Cancel = true;
                        break;
                    }
                }

                Thread.Sleep(10);
            }
        }

        private void ProcessFiles(string directory)
        {
            DirectoryInfo d = new DirectoryInfo(directory);

            FileSystemInfo[] infos = d.GetFileSystemInfos();

            foreach (FileSystemInfo item in infos)
            {
                if (item is DirectoryInfo)
                {
                    ProcessFiles(item.FullName);
                }
                else
                {
                    ParseFile(item.FullName);
                }
            }
        }

        private void BackgroundWorkerProcessData_DoWork(object sender, DoWorkEventArgs e)
        {
            BackgroundWorker bgWorker = sender as BackgroundWorker;

            while (true)
            {
                try
                {
                    int dataCount = dataQueue.Count;

                    if (dataCount > WindowSize)
                    {

                        float[,] channels = new float[WindowSize, 8];

                        for (int i = 0; i < WindowSize; i++)
                        {
                            float[] line;

                            bool success = dataQueue.TryDequeue(out line);
                            if (success)
                            {
                                for (int j = 0; j < 8; j++)
                                {
                                    channels[i, j] = line[j];
                                }
                            }
                        }
                        string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        ProcessFrame(channels, stamp);
                    }
                }
                catch (Exception ex)
                {
                    using (StreamWriter sw = new StreamWriter(@"ErrLog.txt", true))
                    {
                        sw.WriteLine(ex.Message + " \r\n" + ex.StackTrace.ToString());
                        sw.WriteLine("---------------------------------------------------------");
                        sw.Close();
                    }
                }



                if (bgWorker.CancellationPending == true)
                {
                    e.Cancel = true;
                    break;
                }

                Thread.Sleep(3000);
            }
        }

        /// <summary>
        ///   This method will be called whenever there is a new audio
        ///   frame to be processed.
        /// </summary>
        /// 
        void ProcessSingleFrameTest(float[,] channels)
        {
            // We can start by converting the audio frame to a complex signal
            //ComplexSignal signal = ComplexSignal.FromSignal(eventArgs.Signal);
            //Signal realSignal = Signal.FromArray(channels, WindowSize, 8, 50, SampleFormat.Format32BitIeeeFloat);
            //ComplexSignal signal = ComplexSignal.FromSignal(realSignal);

            ComplexSignal signal = ComplexSignal.FromArray(channels, 50);

            // If its needed,
            if (window != null)
            {
                // Apply the chosen audio window
                signal = window.Apply(signal, 0);
            }

            // Transform to the complex domain
            signal.ForwardFourierTransform();

            // Now we can get the power spectrum output and its
            // related frequency vector to plot our spectrometer.

            double[] freqv = Tools.GetFrequencyVector(signal.Length, signal.SampleRate);

            //double[][] power = new double[8][];

            Complex[] channel0 = signal.GetChannel(0);
            Complex[] channel1 = signal.GetChannel(1);
            Complex[] channel2 = signal.GetChannel(2);

            double[] g0 = Tools.GetPowerSpectrum(channel0);
            double[] g1 = Tools.GetPowerSpectrum(channel1);
            double[] g2 = Tools.GetPowerSpectrum(channel2);

            //for(int i = 0; i < 8; i++)
            //{
            //    Complex[] channel = signal.GetChannel(i);
            //    power[i] = Tools.GetPowerSpectrum(channel);
            //    // zero DC
            //    power[i][0] = 0;
            //}

            if (chart1.InvokeRequired)
            {
                chart1.BeginInvoke(new MethodInvoker(() =>
                {
                    chart1.Series[4].Points.Clear();
                    chart1.Series[5].Points.Clear();
                    chart1.Series[6].Points.Clear();
                    for (int i = 0; i < g0.Length; i++)
                    {
                        chart1.Series[4].Points.AddXY(freqv[i], g0[i]);
                        chart1.Series[5].Points.AddXY(freqv[i], g1[i]);
                        chart1.Series[6].Points.AddXY(freqv[i], g2[i]);

                    }
                    chart1.Invalidate();
                }));
            }
            else
            {
                chart1.Series[1].Points.Clear();
                for (int i = 0; i < g0.Length; i++)
                {
                    chart1.Series[1].Points.AddXY(freqv[i], g0[i]);

                }
                chart1.Invalidate();
            }
        }

        /// <summary>
        ///   This method will be called whenever there is a new audio
        ///   frame to be processed.
        /// </summary>
        /// 
        void ProcessSingleFrame(float[,] channels)
        {
            //float[] data = new float[WindowSize];
            //// We can start by converting the audio frame to a complex signal
            //for(int i = 0; i < WindowSize; i++)
            //{
            //    data[i] = channels[1, i];
            //}
            //Signal realSignal = Signal.FromArray(data, 50, SampleFormat.Format32BitIeeeFloat);
            //ComplexSignal signal = ComplexSignal.FromSignal(realSignal);

            ComplexSignal signal = ComplexSignal.FromArray(channels, 50);

            // If its needed,
            if (window != null)
            {
                // Apply the chosen audio window
                signal = window.Apply(signal, 0);
            }

            // Transform to the complex domain
            signal.ForwardFourierTransform();

            // Now we can get the power spectrum output and its
            // related frequency vector to plot our spectrometer.

            double[] freqv = Tools.GetFrequencyVector(signal.Length, signal.SampleRate);

            //double[][] power = new double[8][];

            Complex[] channel0 = signal.GetChannel(0);
            Complex[] channel1 = signal.GetChannel(1);
            Complex[] channel2 = signal.GetChannel(2);
            Complex[] channel3 = signal.GetChannel(3);

            double[] g0 = Tools.GetPowerSpectrum(channel0);
            double[] g1 = Tools.GetPowerSpectrum(channel1);
            double[] g2 = Tools.GetPowerSpectrum(channel2);
            double[] g3 = Tools.GetPowerSpectrum(channel3);

            g0[0] = 0;
            g1[0] = 0;
            g2[0] = 0;
            g3[0] = 0;

            //for(int i = 0; i < 8; i++)
            //{
            //    Complex[] channel = signal.GetChannel(i);
            //    power[i] = Tools.GetPowerSpectrum(channel);
            //    // zero DC
            //    power[i][0] = 0;
            //}

            if (chart1.InvokeRequired)
            {
                chart1.BeginInvoke(new MethodInvoker(() =>
                {
                    chart1.Series[4].Points.Clear();
                    chart1.Series[5].Points.Clear();
                    chart1.Series[6].Points.Clear();
                    chart1.Series[7].Points.Clear();
                    for (int i = 0; i < g0.Length; i++)
                    {
                        chart1.Series[4].Points.AddXY(freqv[i], g0[i]);
                        chart1.Series[5].Points.AddXY(freqv[i], g1[i]);
                        chart1.Series[6].Points.AddXY(freqv[i], g2[i]);
                        chart1.Series[7].Points.AddXY(freqv[i], g3[i]);
                    }
                    chart1.Invalidate();
                }));
            }
            else
            {
                chart1.Series[4].Points.Clear();
                chart1.Series[5].Points.Clear();
                chart1.Series[6].Points.Clear();
                chart1.Series[7].Points.Clear();
                for (int i = 0; i < g0.Length; i++)
                {
                    chart1.Series[4].Points.AddXY(freqv[i], g0[i]);
                    chart1.Series[5].Points.AddXY(freqv[i], g1[i]);
                    chart1.Series[6].Points.AddXY(freqv[i], g2[i]);
                    chart1.Series[7].Points.AddXY(freqv[i], g3[i]);
                }
                chart1.Invalidate();
            }
        }

        /// <summary>
        ///   This method will be called whenever there is a new audio
        ///   frame to be processed.
        /// </summary>
        /// 
        void ProcessFrame(float[,] channels, string stamp)
        {
            // We can start by converting the audio frame to a complex signal

            //Signal realSignal = Signal.FromArray(channels,WindowSize,8, 50, SampleFormat.Format32BitIeeeFloat);
            //ComplexSignal signal = ComplexSignal.FromSignal(realSignal);
            ComplexSignal signal = ComplexSignal.FromArray(channels, 50);

            // If its needed,
            if (window != null)
            {
                // Apply the chosen audio window
                signal = window.Apply(signal, 0);
            }

            // Transform to the complex domain
            signal.ForwardFourierTransform();

            // Now we can get the power spectrum output and its
            // related frequency vector to plot our spectrometer.

            double[] freqv = Tools.GetFrequencyVector(signal.Length, signal.SampleRate);

            double[][] power = new double[signal.Channels][];

            //Complex[] channel = signal.GetChannel(0);

            //double[] g = Tools.GetPowerSpectrum(channel);

            int[][] peaksIndex1 = new int[signal.Channels][];
            int[][] peaksIndex2 = new int[signal.Channels][];

            int SearchLength = 7;

            string content = stamp + ",";

            for (int i = 0; i < signal.Channels; i++)
            {
                //complexChannels[i] = signal.GetChannel(i);
                power[i] = Tools.GetPowerSpectrum(signal.GetChannel(i));

                // zero DC
                power[i][0] = 0;

                double max = power[i].Max();
                int position = Array.IndexOf(power[i], max);

                //normalize amplitude
                for (int n = 0; n < power[i].Length; n++)
                {
                    power[i][n] = power[i][n] / max;
                }

                double maxFrequency = freqv[position];


                peaksIndex1[i] = power[i].FindPeaks();

                if (peaksIndex1[i].Length < SearchLength)
                {
                    return;
                }

                double[] peaks2 = new double[peaksIndex1[i].Length];
                for (int j = 0; j < peaksIndex1[i].Length; j++)
                {
                    peaks2[j] = power[i][peaksIndex1[i][j]];
                    //low pass
                    //if (freqv[peaksIndex1[i][j]] > 10)
                    //{
                    //    peaks2[j] = 0;
                    //}
                }

                peaksIndex2[i] = MaxSort(SearchLength, peaks2);

                Array.Sort(peaksIndex2[i]);

                //int[] index = peaks2.FindPeaks();
                //int[] rawIndex = new int[index.Length];
                double[] frequencies = new double[SearchLength];

                try
                {
                    for (int k = 0; k < SearchLength; k++)
                    {
                        frequencies[k] = freqv[peaksIndex1[i][peaksIndex2[i][k]]];
                    }
                }
                catch (Exception ex)
                {
                }

                double frequency = 0;

                double frequency1 = FindFFWithFundamentalFreqency(frequencies, maxFrequency);

                List<double> candidateFrequencies = SearchCandidateFrequencyWithSpacing(frequencies, frequencies.Length, 0.3);

                double frequency2 = FindFFWithSpacing(frequency1, candidateFrequencies.ToArray());

                if (Math.Abs(frequency1 - frequency2) < 0.15)
                {
                    frequency = (frequency2 + frequency1) / 2;
                }

                //double force = Math.Round(4 * refDensity * refLength[i]* refLength[i]* frequency* frequency/1000,2);
                //if(frequency == 0)
                //{
                //    frequency = refFrequency[i];
                //}

                content += frequency.ToString();
                content += ",";

                //AppendLog("Channel " + (i + 1).ToString() + " ");
                if ((i + 1) == (int)numericUpDownChannel.Value)
                {
                    AppendLog("Channel " + (i + 1).ToString() + " f1: " + frequency1.ToString() + " f2: " + frequency2.ToString() + " Fundamental frequency:" + frequency.ToString());
                }
            }
            content = content.Remove(content.Length - 1);

            //AppendResult(content);

            //return;
            if (chart1.InvokeRequired)
            {
                chart1.BeginInvoke(new MethodInvoker(() =>
                {
                    for (int j = 0; j < signal.Channels; j++)
                    {
                        chart1.Series[j + 16].Points.Clear();
                        for (int i = 0; i < freqv.Length; i++)
                        {
                            chart1.Series[j + 16].Points.AddXY(freqv[i], power[j][i]);
                        }
                        for (int k = 0; k < peaksIndex2[j].Length; k++)
                        {
                            chart1.Series[j + 16].Points[peaksIndex1[j][peaksIndex2[j][k]]].Label = freqv[peaksIndex1[j][peaksIndex2[j][k]]].ToString();
                        }
                    }
                    chart1.Invalidate();
                }));
            }
            else
            {
                for (int j = 0; j < signal.Channels; j++)
                {
                    chart1.Series[j + 16].Points.Clear();
                    for (int i = 0; i < freqv.Length; i++)
                    {
                        chart1.Series[j + 16].Points.AddXY(freqv[i], power[j][i]);
                    }
                }
                chart1.Invalidate();
            }

        }

        private double FindFFWithFundamentalFreqency(double[] frequencies, double amplitudeMaxFreq)
        {
            //double[] frequencies = {1.796875,3.203125,3.59375,5.3515625,5.46875,7.148375,7.265625 };
            //double amplitudeMaxFreq = 7.148375;

            List<double> candidateFreq = new List<double>();
            double temp;
            int n = 1;
            while ((temp = amplitudeMaxFreq / n) > 1.5)
            {
                candidateFreq.Add(temp);
                n++;
            }

            int length = candidateFreq.Count;

            if (length == 0)
            {
                return 0;
            }

            double[][] quotients = new double[length][];

            for (int i = 0; i < length; i++)
            {
                quotients[i] = CalculateQuotients(frequencies, candidateFreq[i]);
            }

            double[][] error = new double[length][];
            double[] sum = new double[length];
            int[] index = new int[length];
            for (int i = 0; i < length; i++)
            {
                error[i] = CalculateError(quotients[i], 0.05, out sum[i]);
                index[i] = i;
            }

            //int[] index = MaxSort(3, sum);
            double[] key = (double[])(sum.Clone());
            Array.Sort(key, index);
            Array.Reverse(index);
            int count = 0;
            int firstElem = index[0];
            for (int i = 1; i < length; i++)
            {
                if (sum[firstElem] == sum[index[i]])
                {
                    count++;
                }
            }

            double result = candidateFreq[index[count]];

            return result;
        }

        private double[] CalculateError(double[] actual, double precision, out double sum)
        {
            double[] result = new double[actual.Length];
            sum = 0;
            for (int i = 0; i < actual.Length; i++)
            {
                result[i] = Math.Abs(actual[i] - Math.Round(actual[i]));
                if (result[i] <= precision)
                {
                    sum = sum + 1;
                }
            }

            return result;
        }

        private double[] CalculateQuotients(double[] freq, double divisor)
        {
            double[] result = new double[freq.Length];

            for (int i = 0; i < freq.Length; i++)
            {
                result[i] = freq[i] / divisor;
            }

            return result;
        }


        private void Director(string dir)
        {
            DirectoryInfo d = new DirectoryInfo(dir);
            FileSystemInfo[] fsinfos = d.GetFileSystemInfos();
            foreach (FileSystemInfo fsinfo in fsinfos)
            {
                if (fsinfo is DirectoryInfo)     //判断是否为文件夹
                {
                    Director(fsinfo.FullName);//递归调用
                }
                else
                {
                    AppendLog(fsinfo.FullName);//输出文件的全部路径
                }
            }


        }


        /// <summary>
        /// 返回前length 个array中最大的值的索引
        /// </summary>
        /// <param name="length"></param>
        /// <param name="array"></param>
        /// <returns></returns>
        private int[] MaxSort(int length, double[] array)
        {
            //double[] arr = { 1,4,2,6,7,5,9,8,3};
            double[] reference = (double[])array.Clone();

            int[] index;

            if (length < array.Length)
            {
                index = new int[length];
            }
            else
            {
                index = new int[array.Length];
            }


            for (int i = 0; i < array.Length; i++)
            {
                index[i] = Array.IndexOf(reference, array[i]);
                for (int j = i + 1; j < array.Length; j++)
                {
                    if (array[i] < array[j])
                    {
                        index[i] = Array.IndexOf(reference, array[j]);
                        double temp = array[j];
                        array[j] = array[i];
                        array[i] = temp;
                    }
                }
                if (i == (length - 1))
                {
                    break;
                }
            }
            return index;
        }

        /***
         * bak
        void ProcessFrame(float[,] channels)
        {
            // We can start by converting the audio frame to a complex signal

            //Signal realSignal = Signal.FromArray(channels,WindowSize,8, 50, SampleFormat.Format32BitIeeeFloat);
            //ComplexSignal signal = ComplexSignal.FromSignal(realSignal);
            ComplexSignal signal = ComplexSignal.FromArray(channels, 50);

            // If its needed,
            if (window != null)
            {
                // Apply the chosen audio window
                signal = window.Apply(signal, 0);
            }

            // Transform to the complex domain
            signal.ForwardFourierTransform();

            // Now we can get the power spectrum output and its
            // related frequency vector to plot our spectrometer.

            double[] freqv = Tools.GetFrequencyVector(signal.Length, signal.SampleRate);

            double[][] power = new double[signal.Channels][];

            //Complex[] channel = signal.GetChannel(0);

            //double[] g = Tools.GetPowerSpectrum(channel);

            int[][] peaksIndex1 = new int[signal.Channels][];
            int[][] peaksIndex2 = new int[signal.Channels][];

            for (int i = 0; i < signal.Channels; i++)
            {
                //complexChannels[i] = signal.GetChannel(i);
                power[i] = Tools.GetPowerSpectrum(signal.GetChannel(i));

                // zero DC
                power[i][0] = 0;

                double max = power[i].Max();
                int position = Array.IndexOf(power[i], max);

                //normalize amplitude
                for (int n = 0; n < power[i].Length; n++)
                {
                    power[i][n] = power[i][n] / max;
                }

                double maxFrequency = freqv[position];

                peaksIndex1[i] = power[i].FindPeaks();

                double[] peaks2 = new double[peaksIndex1[i].Length];
                for (int j = 0; j < peaksIndex1[i].Length; j++)
                {
                    peaks2[j] = power[i][peaksIndex1[i][j]];
                    if (freqv[peaksIndex1[i][j]] > 12)
                    {
                        peaks2[j] = 0;
                    }
                }
                int[] index = peaks2.FindPeaks();
                int[] rawIndex = new int[index.Length];
                double[] frequencies = new double[index.Length];
                for (int k = 0; k < index.Length; k++)
                {
                    rawIndex[k] = peaksIndex1[i][index[k]];
                    frequencies[k] = freqv[rawIndex[k]];
                }

                peaksIndex2[i] = rawIndex;

                //textBoxLog.AppendText("Channel " + (i + 1).ToString() + " ");
                if ((i + 1) == (int)numericUpDownChannel.Value)
                {
                    AppendLog("Channel " + (i + 1).ToString() + " " + frequencies.Length.ToString());
                    List<double> candidateFrequencies = SearchFundamentalFrequency(frequencies, frequencies.Length, double.Parse(textBoxPort.Text));
                    double frequency = GetFundamentalFrequency(maxFrequency, candidateFrequencies.ToArray());
                    AppendLog("Channel " + (i + 1).ToString() + " maxFrequency:" + maxFrequency + " Fundamental frequency:" + frequency.ToString());
                }
            }

            if (chart1.InvokeRequired)
            {
                chart1.BeginInvoke(new MethodInvoker(() =>
                {
                    for (int j = 0; j < signal.Channels; j++)
                    {
                        chart1.Series[j + 16].Points.Clear();
                        for (int i = 0; i < freqv.Length; i++)
                        {
                            chart1.Series[j + 16].Points.AddXY(freqv[i], power[j][i]);
                        }
                        for (int k = 0; k < peaksIndex2[j].Length; k++)
                        {
                            chart1.Series[j + 16].Points[peaksIndex2[j][k]].Label = freqv[peaksIndex2[j][k]].ToString();
                        }
                    }
                    chart1.Invalidate();
                }));
            }
            else
            {
                for (int j = 0; j < signal.Channels; j++)
                {
                    chart1.Series[j + 16].Points.Clear();
                    for (int i = 0; i < freqv.Length; i++)
                    {
                        chart1.Series[j + 16].Points.AddXY(freqv[i], power[j][i]);
                    }
                    for (int k = 0; k < peaksIndex2[j].Length; k++)
                    {
                        chart1.Series[j + 16].Points[peaksIndex2[j][k]].Label = freqv[peaksIndex2[j][k]].ToString();
                    }
                }
                chart1.Invalidate();
            }

        }
        **/

        /// <summary>
        /// 写记录
        /// </summary>
        /// <param name="str"></param>
        private void AppendRecord(string str)
        {
            //if (!Directory.Exists("ErrLog"))
            //{
            //    Directory.CreateDirectory("ErrLog");
            //}
            string currentDate = DateTime.Now.ToString("yyyy-MM-dd") + ".txt";

            string pathString = Path.Combine(@"D:\vibrate", currentDate);

            using (StreamWriter sw = new StreamWriter(pathString, true))
            {
                sw.WriteLine(str);
                sw.Close();
            }
        }

        public void Start()
        {
            buttonStart.Enabled = false;
            buttonStop.Enabled = true;

            foreach (ACT12x va in deviceList.Values)
            {
                va.Start();
            }
        }

        public void Stop()
        {
            buttonStart.Enabled = true;
            buttonStop.Enabled = false;
            foreach (ACT12x va in deviceList.Values)
            {
                va.Stop();
            }
        }

        private void buttonStart_Click(object sender, EventArgs e)
        {
            Start();
            //AppendRecord("hello world");
            return;
            //string str = DateTime.Now.ToShortDateString();
            //MessageBox.Show(str);
            //return;
            //AccWave aw = new AccWave("123", "acc", new double[] { 12.1, 12.5, 44.6 });
            //var serializer = MessagePackSerializer.Get<AccWave>();
            //byte[] result = serializer.PackSingleObject(aw);
            ////serializer.Pack(stream, aw);
            ////stream.Position = 0;
            //var value = serializer.UnpackSingleObject(result);

            //MessageBox.Show(result.Length+"\r\n"+value.id + " " + value.type + " " + value.data[0].ToString());
            //vact = new VibrationACT12x("1",int.Parse(textBoxPort.Text), chart1, "ACT1228",@"E:\vibrate",this.database,this.textBoxLog);
            //vact.Start();
            //buttonStart.Enabled = false;
            //buttonStop.Enabled = true;
            //return;

            int port;

            bool success = Int32.TryParse(textBoxPort.Text, out port);

            if (!success)
            {
                MessageBox.Show("端口错误");
                return;
            }

            buttonStart.Enabled = false;
            buttonStop.Enabled = true;

            udpClient = new UdpClient(port);
            backgroundWorkerUpdateUI.RunWorkerAsync();
            backgroundWorkerProcessData.RunWorkerAsync();
            backgroundWorkerReceiveData.RunWorkerAsync();
        }

        private void buttonStop_Click(object sender, EventArgs e)
        {
            Stop();
            //vact.Stop();
            //buttonStart.Enabled = true;
            //buttonStop.Enabled = false;
            return;
            try
            {
                buttonStart.Enabled = true;
                buttonStop.Enabled = false;
                backgroundWorkerUpdateUI.CancelAsync();
                backgroundWorkerProcessData.CancelAsync();
                backgroundWorkerReceiveData.CancelAsync();
                udpClient.Close();

            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }

        }

        private void checkedListBoxChannel_SelectedIndexChanged(object sender, EventArgs e)
        {
            int index = checkedListBoxChannel.SelectedIndex;
            if (chart1.Series[index] != null && chart1.Series[index + 16] != null)
            {
                bool selected = checkedListBoxChannel.GetItemChecked(index);
                chart1.Series[index].Enabled = selected;
                chart1.Series[index + 16].Enabled = selected;
            }
            //MessageBox.Show("Index: "+index+" "+checkedListBoxChannel.SelectedItem.ToString()+ " : " + checkedListBoxChannel.GetItemChecked(index).ToString());
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            // 注意判断关闭事件reason来源于窗体按钮，否则用菜单退出时无法退出!
            if (e.CloseReason == CloseReason.UserClosing)
            {
                //取消"关闭窗口"事件
                e.Cancel = true; // 取消关闭窗体 

                //使关闭时窗口向右下角缩小的效果
                this.WindowState = FormWindowState.Minimized;
                this.notifyIcon1.Visible = true;
                this.Hide();
                return;
            }
        }



        private void RestoreToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.Visible = true;
            this.WindowState = FormWindowState.Normal;
            this.notifyIcon1.Visible = true;
            this.Show();
        }

        private void ExitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show("确定要退出？", "系统提示", MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1) == DialogResult.Yes)
            {
                this.notifyIcon1.Visible = false;
                this.Close();
                this.Dispose();
            }
        }

        private void notifyIcon1_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            if (this.Visible)
            {
                this.WindowState = FormWindowState.Minimized;
                this.notifyIcon1.Visible = true;
                this.Hide();
            }
            else
            {
                this.Visible = true;
                this.WindowState = FormWindowState.Normal;
                this.Activate();
            }
        }

        private void button1_Click(object sender, EventArgs e)
        {
            UdpClient udpClient = null;
            IPAddress remoteIp = IPAddress.Parse("112.112.16.144");
            try
            {
                udpClient = new UdpClient();
                udpClient.Connect(remoteIp, 26668);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }

            MessagePackSerializer serializer = MessagePackSerializer.Get<AccWave>();

            AccWave aw = new AccWave("123", "acc", new float[] { 12.1F, 12.5F, 44.6F });
            byte[] result = serializer.PackSingleObject(aw);
            udpClient.Send(result, result.Length);

            MessageBox.Show("Finished");

        }

        private void buttonTest_Click(object sender, EventArgs e)
        {
            //FindFundamentalFs();
            //List<double> mainSeq = new List<double>{ 3.61328125, 5.37109375, 7.12890625, 8.7890625, 10.64453125 };
            //List<double> subSeq1 = new List<double> { 3.61328125, 7.12890625, 10.64453125 };
            //List<double> subSeq2 = new List<double> { 2.24609375, 4.39453125, 2.1484375, 0.9765625 };

            //double ret = FindBestFundamentalFrequency(subSeq2, double.Parse(textBoxPort.Text));
            //MessageBox.Show("BestFundamentalFrequency:" + ret);
            //ret = TestSubSequence(mainSeq, subSeq2);
            //MessageBox.Show("subSeq2 is sub of mainSeq:" + ret);
            //double[] arr = { 1, 4, 2, 6, 7, 5, 9, 8, 3 };
            //Sort(4,arr);
            //semaphore.Release();
            //Director("1");
            return;
            textBoxLog.Clear();
            ParseFile("2018-05-30-14-50.txt");

            textBoxLog.Clear();
            double precision = double.Parse(textBoxPort.Text);
            double[] test = { 1.7578125, 3.515625, 5.2734375, 6.93359375, 8.7890625, 10.546875, 11.1328125, 12.890625, 14.0625, 16.40625, 17.67578125, 19.921875, 21.19140625, 23.046875 };
            //SearchFundamentalFrequency(test, test.Length, precision);
            /*
            StreamReader sr = new StreamReader("2raw.csv", Encoding.UTF8);
            String line;

            float[,] channels = new float[WindowSize, 8];

            int index = 0;
            char[] chs = { ',' };
            while ((line = sr.ReadLine()) != null)
            {
                string[] items = line.Split(chs);

                for (int j = 0; j < 8; j++)
                {
                    channels[index, j] = float.Parse(items[j]);
                }
                index++;
            }
            sr.Close();

            ProcessFrame(channels);
            */
        }

        private void AppendLog(string message)
        {
            if (textBoxLog.InvokeRequired)
            {
                textBoxLog.BeginInvoke(new MethodInvoker(() =>
                {
                    textBoxLog.AppendText(message + " \r\n");
                }));
            }
            else
            {
                textBoxLog.AppendText(message + " \r\n");
            }
        }

        private void ParseFile(string fileName)
        {
            //int WindowSize = 512;
            DateTime dt = DateTime.ParseExact("2018-05-30-14-50-00-025", "yyyy-MM-dd-HH-mm-ss-fff", CultureInfo.CurrentCulture);
            using (StreamReader sr = new StreamReader(fileName, true))
            {
                string line;
                int count = 0;
                // Read and display lines from the file until the end of 
                // the file is reached.
                float[,] channels = new float[WindowSize, 4];

                try
                {
                    while ((line = sr.ReadLine()) != null)
                    {
                        string[] splitedLine = line.Split('\t');
                        if (splitedLine.Length < 4)
                        {
                            continue;
                        }
                        //save start time
                        //dt = DateTime.ParseExact(splitedLine[0], "yyyy-MM-dd-HH-mm-ss-fff", CultureInfo.CurrentCulture);

                        channels[count, 0] = float.Parse(splitedLine[1]);
                        channels[count, 1] = float.Parse(splitedLine[2]);
                        channels[count, 2] = float.Parse(splitedLine[3]);
                        channels[count, 3] = float.Parse(splitedLine[4]);
                        count++;
                        if (count == WindowSize)
                        {
                            dt = DateTime.ParseExact(splitedLine[0], "yyyy-MM-dd-HH-mm-ss-fff", CultureInfo.CurrentCulture);
                            count = 0;

                            ProcessFrame(channels, dt.ToString("yyy-MM-dd HH:mm:ss"));
                            semaphore.WaitOne();
                        }
                    }
                    sr.Close();
                }
                catch (Exception ex)
                {
                }

            }
        }

        private List<double> SearchCandidateFrequencyWithSpacing(double[] frequencies, int length, double precision)
        {
            List<List<double>> temp = new List<List<double>>();
            List<double> fundamentalFreqv = new List<double>();
            for (int i = 0; i < length - 1; i++)
            {
                for (int j = i + 1; j < length - 1; j++)
                {
                    List<double> tmpArr = new List<double>();
                    List<double> tmpDelta = new List<double>();
                    tmpArr.Add(frequencies[i]);
                    tmpArr.Add(frequencies[j]);
                    double delta = frequencies[j] - frequencies[i];
                    tmpDelta.Add(delta);
                    double tmpData = frequencies[j];
                    for (int k = j + 1; k < length - 1; k++)
                    {
                        if (Math.Abs(frequencies[k] - tmpData - delta) < precision)
                        {
                            delta = frequencies[k] - tmpData;
                            tmpDelta.Add(frequencies[k] - tmpData);
                            tmpData = frequencies[k];
                            tmpArr.Add(frequencies[k]);
                        }
                    }

                    if (tmpArr.Count > 2)
                    {
                        //temp.Add(tmpDelta);
                        string content = null;
                        fundamentalFreqv.Add(tmpDelta.Average());
                        content = "Delta: " + tmpDelta.Average() + "\r\n[";
                        //foreach (double item in tmpArr)
                        //{
                        //    content = "Delta: " + delta + "\r\n[";
                        //    content += item.ToString();
                        //    content += " ";
                        //    //AppendLog(
                        //}
                        for (int m = 0; m < tmpArr.Count; m++)
                        {
                            content += tmpArr[m].ToString();
                            content += " ";
                        }
                        content += "]\r\n";
                        //AppendLog(content);
                        //textBoxLog.AppendText(content);
                    }
                }
            }
            if (fundamentalFreqv.Count != 0)
            {

                //AppendLog("Fundamental Frequency: " + temp[indexOfLongest].Average().ToString());
            }

            return fundamentalFreqv;

            #region
            /*
             *List<int> dupIndex = new List<int>();
                for(int i = 0; i < temp.Count; i++)
                {
                    List<double> mainSeq = temp[i];
                    for(int j = i + 1; j < temp.Count; j++)
                    {
                        bool ret = TestSubSequence(temp[i], temp[j]);
                        if (ret)
                        {
                            dupIndex.Add(j);
                        }
                    }
                }

                for(int i = 0; i < dupIndex.Count; i++)
                {
                    temp.RemoveAt(dupIndex[i]);
                }

                int indexOfLongest = 0;
                for (int n = 1; n < temp.Count; n++)
                {
                    //indexOfLongest = 0;
                    if (temp[n].Count > temp[indexOfLongest].Count)
                    {
                        indexOfLongest = n;
                    }
                }
             */
            #endregion
        }

        //private double GetFundamentalFrequency(double maxFrequency,double[] candidateFrequencies)
        //{
        //    int n = 1;
        //    double f1 = 0;
        //    while ((f1 = maxFrequency / n) > 1)
        //    {
        //        for(int i = 0; i < candidateFrequencies.Length; i++)
        //        {
        //            double candidate = candidateFrequencies[i];
        //            if (Math.Abs(candidate-f1) < 0.1)
        //            {
        //                return f1;
        //            }
        //        }
        //        n++;
        //    }
        //    return 0;
        //}

        private double FindFFWithSpacing(double frequency, double[] candidateFrequencies)
        {
            if (frequency == 0 || candidateFrequencies.Length == 0)
            {
                return 0;
            }

            //double[] diff = new double[freqvs.Count];
            double min = 0;
            int minIndex = 0;

            double diff = Math.Abs(frequency - candidateFrequencies[0]);
            for (int j = 1; j < candidateFrequencies.Length; j++)
            {
                double diff_temp = Math.Abs(frequency - candidateFrequencies[j]);
                if (diff > diff_temp)
                {
                    diff = diff_temp;
                    minIndex = j;
                }
            }

            //if (diff < 0.15)
            //{
            return candidateFrequencies[minIndex];
            //}
            //else
            //{
            //    return 0;
            //}
        }

        private double GetFundamentalFrequency(double maxFrequency, double[] candidateFrequencies)
        {
            List<double> freqvs = new List<double>();

            double f1 = 0;
            int n = 1;
            while ((f1 = maxFrequency / n) > 1)
            {
                freqvs.Add(f1);
                n++;
            }

            if (freqvs.Count == 0 || candidateFrequencies.Length == 0)
            {
                return 0;
            }

            double[] diff = new double[freqvs.Count];
            double min = 0;
            int minIndex = 0;

            for (int i = 0; i < freqvs.Count; i++)
            {
                diff[i] = Math.Abs(freqvs[i] - candidateFrequencies[0]);
                for (int j = 1; j < candidateFrequencies.Length; j++)
                {
                    double diff_temp = Math.Abs(freqvs[i] - candidateFrequencies[j]);
                    if (diff[i] > diff_temp)
                    {
                        diff[i] = diff_temp;
                    }
                }
            }

            min = diff[0];
            minIndex = 0;
            for (int k = 1; k < diff.Length; k++)
            {
                if (diff[k] < min)
                {
                    min = diff[k];
                    minIndex = k;
                }
            }

            return freqvs[minIndex];
        }

        //reference
        private double GetFundamentalFrequency(int refChannel, double[] candidateFrequencies)
        {
            if (candidateFrequencies.Length == 0)
            {
                return 0;
            }

            double[] diff = new double[candidateFrequencies.Length];
            double min = 0;
            int minIndex = 0;

            for (int i = 0; i < candidateFrequencies.Length; i++)
            {
                diff[i] = 0;// CalculateError(candidateFrequencies[i], refFrequency[refChannel]);
                            //Math.Abs(refFrequency[refChannel] - candidateFrequencies[i]);
            }

            min = diff[0];
            minIndex = 0;
            for (int k = 1; k < diff.Length; k++)
            {
                if (diff[k] < min)
                {
                    min = diff[k];
                    minIndex = k;
                }
            }

            //return candidateFrequencies[minIndex];

            if (diff[minIndex] < 0.1)
            {
                return candidateFrequencies[minIndex];
            }
            else
            {
                return 0;
            }

        }

        private void AppendResult(string content)
        {
            using (StreamWriter sw = new StreamWriter("result.txt", true))
            {
                sw.WriteLine(content);
                sw.Close();
            }
        }

        private double CalculateError(double actualValue, double desireValue)
        {
            return Math.Abs(actualValue - desireValue) / desireValue;
        }

        private double FindBestFundamentalFrequency(List<double> frequencyList, double referenceFrequency)
        {
            double bestFreqency = 0;
            int indexOfBest = 0;
            double diff = Math.Abs(frequencyList[0] - referenceFrequency);
            for (int i = 1; i < frequencyList.Count; i++)
            {
                double ret = Math.Abs(frequencyList[i] - referenceFrequency);
                if (ret < diff)
                {
                    diff = ret;
                    indexOfBest = i;
                }
            }
            bestFreqency = frequencyList[indexOfBest];
            return bestFreqency;
        }

        private bool TestSubSequence(List<double> mainSeq, List<double> subSeq)
        {
            if (mainSeq.Count > subSeq.Count)
            {
                double first = subSeq[0];
                int index = mainSeq.IndexOf(first);
                if (index != -1)
                {
                    int length = mainSeq.Count - index;
                    if (length == subSeq.Count)
                    {
                        for (int i = 0; i < length; i++)
                        {
                            if (mainSeq[index + i] != subSeq[i])
                            {
                                return false;
                            }
                        }
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
                else
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }
    }
}
