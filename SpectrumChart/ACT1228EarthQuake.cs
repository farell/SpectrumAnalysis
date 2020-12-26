using Accord.Audio;
using Accord.Audio.Windows;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;
using System.Data.SQLite;
using MsgPack.Serialization;

namespace SpectrumChart
{
    class ACT1228EarthQuake : ACT12x
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private string ip;
        private int udpPort;
        private ConcurrentQueue<float[]> dataQueue;
        private ConcurrentQueue<float[]> uiQueue;
        private ConcurrentQueue<string> rawQueue;
        private ConcurrentQueue<string> historyQueue;
        private ConcurrentQueue<DataValue> resultQueue;
        private BackgroundWorker backgroundWorkerProcessData;
        private BackgroundWorker backgroundWorkerUpdateUI;
        private BackgroundWorker backgroundWorkerReceiveData;
        private UdpClient udpClient;
        private UdpClient udpSendWave;
        private TextBox textBoxLog;
        private IWindow window;
        private const int WindowSize = 2048;
        private Chart chart1;
        private string deviceType;
        private Dictionary<int, VibrateChannel> vibrateChannels;

        private System.Timers.Timer hourTimer;
        private System.Timers.Timer minuteTimer;

        private string basePath;
        private string database;
        private string deviceId;

        private MessagePackSerializer serializer;

        private string spectrumIP;
        private int spectrumPort;

        private bool isUpdateChart;
        private bool isCalculateCableForce;
        private bool isSaveRawData;
        private string Tag;
        private double Threshold;

        public ACT1228EarthQuake(string id, string ip, int port, Chart chart, string deviceType, string path, string database, TextBox tb,double threshold, StackExchange.Redis.ConnectionMultiplexer redis, string spectrumIP, int spectrumPort)
        {
            this.Tag = ip + " : ";
            dataQueue = new ConcurrentQueue<float[]>();
            uiQueue = new ConcurrentQueue<float[]>();
            rawQueue = new ConcurrentQueue<string>();
            historyQueue = new ConcurrentQueue<string>();
            Threshold = threshold;
            backgroundWorkerProcessData = new BackgroundWorker();
            backgroundWorkerReceiveData = new BackgroundWorker();
            backgroundWorkerUpdateUI = new BackgroundWorker();
            backgroundWorkerProcessData.WorkerSupportsCancellation = true;
            backgroundWorkerProcessData.DoWork += BackgroundWorkerProcessData_DoWork;

            backgroundWorkerUpdateUI.WorkerSupportsCancellation = true;
            backgroundWorkerUpdateUI.DoWork += BackgroundWorkerUpdateUI_DoWork;

            backgroundWorkerReceiveData.WorkerSupportsCancellation = true;
            backgroundWorkerReceiveData.DoWork += BackgroundWorkerReceiveData_DoWork;

            serializer = MessagePackSerializer.Get<AccWave>();

            hourTimer = new System.Timers.Timer(1000 * 60 * 60 * 1);
            hourTimer.Elapsed += new System.Timers.ElapsedEventHandler(HourTimer_TimesUp);
            hourTimer.AutoReset = true; //每到指定时间Elapsed事件是触发一次（false），还是一直触发（true）
            minuteTimer = new System.Timers.Timer(1000 * 60 * 2);
            minuteTimer.Elapsed += new System.Timers.ElapsedEventHandler(MinuteTimer_TimesUp);
            minuteTimer.AutoReset = false; //每到指定时间Elapsed事件是触发一次（false），还是一直触发（true）

            window = RaisedCosineWindow.Hann(WindowSize);

            this.ip = ip;
            this.deviceId = id;
            this.deviceType = deviceType;
            this.udpPort = port;
            this.chart1 = chart;
            this.textBoxLog = tb;
            this.basePath = path;
            this.database = database;
            this.spectrumIP = spectrumIP;
            this.spectrumPort = spectrumPort;
            this.isUpdateChart = false;
            this.vibrateChannels = new Dictionary<int, VibrateChannel>();

            this.Threshold = threshold;

            LoadChannels();
        }

        private void LoadChannels()
        {
            using (SQLiteConnection connection = new SQLiteConnection(this.database))
            {
                connection.Open();
                string strainStatement = "select SensorId,ChannelNo,Length,Mass from Channels where GroupNo ='" + this.deviceId + "'";
                SQLiteCommand command = new SQLiteCommand(strainStatement, connection);
                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        //string  groupId = reader.GetString(0);
                        string sensorId = reader.GetString(0);
                        int channelNo = reader.GetInt32(1);
                        double length = reader.GetDouble(2);
                        double mass = reader.GetDouble(3);

                        VibrateChannel vc = new VibrateChannel(sensorId, channelNo, length, mass);

                        vibrateChannels.Add(channelNo, vc);
                    }
                }
            }
        }

        public override void Start()
        {
            udpClient = new UdpClient(udpPort);
            //udpSendWave = new UdpClient("182.245.124.106", 9002);
            backgroundWorkerUpdateUI.RunWorkerAsync();
            backgroundWorkerProcessData.RunWorkerAsync();
            backgroundWorkerReceiveData.RunWorkerAsync();
            //backgroundWorkerSaveRawData.RunWorkerAsync();
            //hourTimer.Start();
            //minuteTimer.Start();
            isSaveRawData = false;
        }

        public override void Stop()
        {
            backgroundWorkerUpdateUI.CancelAsync();
            backgroundWorkerProcessData.CancelAsync();
            backgroundWorkerReceiveData.CancelAsync();
            //backgroundWorkerSaveRawData.CancelAsync();
            udpClient.Close();
            //udpSendWave.Close();
            //hourTimer.Stop();
            isSaveRawData = false;
            minuteTimer.Start();
        }

        private void HourTimer_TimesUp(object sender, System.Timers.ElapsedEventArgs e)
        {
            isSaveRawData = true;
            minuteTimer.Start();

        }

        private void MinuteTimer_TimesUp(object sender, System.Timers.ElapsedEventArgs e)
        {
            isSaveRawData = false;
            int numOfQueueCount = rawQueue.Count;
            string line;
            string fileName = DateTime.Now.ToString("yyyy-MM-dd-HH-mm") + ".csv";
            string pathString = Path.Combine(basePath, fileName);
            StreamWriter sw = new StreamWriter(pathString, true);

            for (int i = 0; i < historyQueue.Count; i++)
            {
                bool success = historyQueue.TryDequeue(out line);
                if (success)
                {
                    sw.Write(line);
                }
            }

            for (int i = 0; i < numOfQueueCount; i++)
            {
                bool success = rawQueue.TryDequeue(out line);
                if (success)
                {
                    sw.Write(line);
                }
            }

            sw.Close();
        }

        private void backgroundWorkerSaveRawData_DoWork(object sender, DoWorkEventArgs e)
        {
            BackgroundWorker bgWorker = sender as BackgroundWorker;
            uint count = 0;
            uint fileSize = 50 * 60 * 5;

            while (true)
            {
                try
                {
                    int dataCount = rawQueue.Count;

                    if (dataCount > fileSize)
                    {
                        string line;
                        string fileName = DateTime.Now.ToString("yyyy-MM-dd-HH-mm");
                        string pathString = Path.Combine(basePath, fileName);
                        StreamWriter sw = new StreamWriter(pathString, true);

                        for (int i = 0; i < fileSize; i++)
                        {
                            bool success = rawQueue.TryDequeue(out line);
                            if (success)
                            {
                                sw.Write(line);
                            }
                        }

                        sw.Close();
                    }
                }
                catch (Exception ex)
                {
                    log.Error(Tag, ex);
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

        private void BackgroundWorkerUpdateUI_DoWork(object sender, DoWorkEventArgs e)
        {
            BackgroundWorker bgWorker = sender as BackgroundWorker;

            //MessagePackSerializer serializer = MessagePackSerializer.Get<AccWave>();

            if (textBoxLog.InvokeRequired)
            {
                textBoxLog.BeginInvoke(new MethodInvoker(() =>
                {
                    textBoxLog.AppendText("Start UI thread dequeue\r\n");
                }));
            }
            else
            {
                textBoxLog.AppendText("Start UI thread dequeue\r\n");
            }

            while (true)
            {
                try
                {
                    float[] data;
                    bool success = uiQueue.TryDequeue(out data);

                    if (success)
                    {
                        UpdateAmplitudeChart(data);
                    }
                    else
                    {
                        Thread.Sleep(10);
                    }

                    if (bgWorker.CancellationPending == true)
                    {
                        e.Cancel = true;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    textBoxLog.AppendText(ex.ToString());
                    if (bgWorker.CancellationPending == true)
                    {
                        e.Cancel = true;
                        break;
                    }
                }

                if (bgWorker.CancellationPending == true)
                {
                    e.Cancel = true;
                    break;
                }
            }
        }

        private void UpdateAmplitudeChart(float[] data)
        {
            int numberOfPointsInChart = 4096;
            if (isUpdateChart)
            {
                if (chart1.InvokeRequired)
                {
                    chart1.BeginInvoke(new MethodInvoker(() =>
                    {
                        for (int i = 0; i < data.Length; i++)
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
                    for (int i = 0; i < data.Length; i++)
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
                }
            }
        }

        private float ACT1228ExtractChannel(byte higher, byte lower)
        {
            int data = (higher & 0x7f) * 256 + lower;

            if ((higher & 0x80) == 0x80)
            {
                data = -data;
            }

            float channel = (float)(data / 10000.0);
            return channel;
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

        private float ACT12816ExtractChannel(byte higher, byte lower)
        {
            int data = (higher & 0x7f) * 256 + lower;

            if ((higher & 0x80) == 0x80)
            {
                data = -data;
            }

            float channel = (float)(data / 1000.0);
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

                    // Blocks until a message returns on this socket from a remote host.
                    Byte[] receiveBytes = udpClient.Receive(ref RemoteIpEndPoint);

                    string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

                    float[] data;
                    data = new float[8];

                    StringBuilder sb = new StringBuilder(1024);
                    sb.Append(stamp + ",");

                    for (int i = 0; i < 8; i++)
                    {
                        data[i] = ACT1228ExtractChannel(receiveBytes[i * 2 + 4], receiveBytes[i * 2 + 5]);
                        if (Math.Abs(data[i]) > Threshold)
                        {
                            StartCapture();
                        }

                        sb.Append(data[i].ToString() + ",");
                    }

                    sb.Remove(sb.Length - 1, 1);
                    sb.Append("\r\n");

                    if (isSaveRawData)
                    {
                        rawQueue.Enqueue(sb.ToString());
                    }
                    else
                    {
                        historyQueue.Enqueue(sb.ToString());
                        if(historyQueue.Count > 6000)
                        {
                            string item;
                            historyQueue.TryDequeue(out item);
                        }
                    }
                    

                    dataQueue.Enqueue(data);
                    uiQueue.Enqueue(data);


                    if (bgWorker.CancellationPending == true)
                    {
                        e.Cancel = true;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    log.Error(Tag, ex);
                    if (bgWorker.CancellationPending == true)
                    {
                        e.Cancel = true;
                        break;
                    }
                }
                if (bgWorker.CancellationPending == true)
                {
                    e.Cancel = true;
                    break;
                }
            }
        }

        private void StartCapture()
        {
            if (!isSaveRawData)
            {
                isSaveRawData = true;
                minuteTimer.Start();
            }
        }

        private void BackgroundWorkerProcessData_DoWork(object sender, DoWorkEventArgs e)
        {
            BackgroundWorker bgWorker = sender as BackgroundWorker;

            uint count = 0;

            while (true)
            {
                try
                {
                    int dataCount = dataQueue.Count;

                    if (dataCount > WindowSize)
                    {
                        int numberOfChannels = 8;


                        float[,] channels = new float[WindowSize, numberOfChannels];

                        StringBuilder sb = new StringBuilder(1024 * 16);

                        for (int i = 0; i < WindowSize; i++)
                        {
                            float[] line;

                            bool success = dataQueue.TryDequeue(out line);
                            if (success)
                            {
                                for (int j = 0; j < numberOfChannels; j++)
                                {
                                    channels[i, j] = line[j];
                                }                                
                            }
                        }
                        //AppendRecord(sb, count + "raw");
                        count++;
                        string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        ProcessFrame(channels, stamp);
                    }
                }
                catch (Exception ex)
                {
                    log.Error(Tag, ex);
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

            IPAddress remoteIp = IPAddress.Parse(this.spectrumIP);
            IPEndPoint remoteEndPoint = new IPEndPoint(remoteIp, spectrumPort);
            UdpClient udpClient = new UdpClient();

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

                if (vibrateChannels.ContainsKey(i + 1))
                {
                    VibrateChannel vc = vibrateChannels[i + 1];
                    float[] floatList = power[i].Select(x => (float)x).ToArray();
                    AccWave awObject = new AccWave(vc.SensorId, "028", floatList);
                    byte[] result = serializer.PackSingleObject(awObject);
                    //AppendLog(this.ip + " Frame Length: " + result.Length.ToString());
                    udpClient.Send(result, result.Length, remoteEndPoint);
                    //udpClient.Close();
                }

                //if (!isCalculateCableForce)
                //{
                //    continue;
                //}

                double maxFrequency = freqv[position];


                peaksIndex1[i] = power[i].FindPeaks();

                if (peaksIndex1[i].Length < SearchLength)
                {
                    continue;
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
            }
            udpClient.Close();

            if (isUpdateChart)
            {
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

                            if (isCalculateCableForce)
                            {
                                for (int k = 0; k < peaksIndex2[j].Length; k++)
                                {
                                    chart1.Series[j + 16].Points[peaksIndex1[j][peaksIndex2[j][k]]].Label = freqv[peaksIndex1[j][peaksIndex2[j][k]]].ToString();
                                }
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

        public override void SetUpdateChart(bool state)
        {
            isUpdateChart = state;
            //AppendLog(this.deviceId + " State:" + isUpdateChart);
        }

        /// <summary>
        /// 写记录
        /// </summary>
        /// <param name="str"></param>
        private void AppendRecord(StringBuilder sb, string fileName)
        {
            //if (!Directory.Exists("ErrLog"))
            //{
            //    Directory.CreateDirectory("ErrLog");
            //}
            //string currentDate = DateTime.Now.ToString("yyyy-MM-dd") + ".txt";

            string currentDate = fileName + ".csv";

            string pathString = Path.Combine(this.basePath, currentDate);

            using (StreamWriter sw = new StreamWriter(pathString, true))
            {
                //StringBuilder sb = new StringBuilder(20);
                sw.Write(sb);
                //sw.WriteLine(sb);
                sw.Close();
            }
        }
    }
}
