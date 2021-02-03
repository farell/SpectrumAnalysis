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
using StackExchange.Redis;

namespace SpectrumChart
{
    class ACT1228CableForce : ACT12x
    {
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private string ip;
        private string localIP;
        private int udpPort;
        private ConcurrentQueue<float[]> dataQueue;
        private ConcurrentQueue<float[]> uiQueue;
        private ConcurrentQueue<string> rawQueue;
        private BackgroundWorker backgroundWorkerProcessData;
        private BackgroundWorker backgroundWorkerUpdateUI;
        private BackgroundWorker backgroundWorkerReceiveData;
        private BackgroundWorker backgroundWorkerSaveRawData;
        private ConnectionMultiplexer redis;
        private double Threshold;
        private UdpClient udpClient;
        private TextBox textBoxLog;
        private IWindow window;
        private const int WindowSize = 1024;
        private Chart chart1;
        private string deviceType;
        private Dictionary<int, VibrateChannel> vibrateChannels;

        private System.Timers.Timer hourTimer;
        private System.Timers.Timer minuteTimer;

        private string basePath;
        private string database;
        private string deviceId;

        private bool isUpdateChart;
        private bool isCalculateCableForce;
        private bool isSaveRawData;
        private IDatabase db;
        //
        private string Tag;

        public ACT1228CableForce(string id, string ip,string localIP, int port, Chart chart, string deviceType, string path, string database, TextBox tb, double threshold, ConnectionMultiplexer redis)
        {
            this.Tag = ip + " : ";
            dataQueue = new ConcurrentQueue<float[]>();
            uiQueue = new ConcurrentQueue<float[]>();
            rawQueue = new ConcurrentQueue<string>();
            this.redis = redis;
            Threshold = threshold;
            backgroundWorkerProcessData = new BackgroundWorker();
            backgroundWorkerReceiveData = new BackgroundWorker();
            backgroundWorkerUpdateUI = new BackgroundWorker();
            backgroundWorkerSaveRawData = new BackgroundWorker();

            backgroundWorkerProcessData.WorkerSupportsCancellation = true;
            backgroundWorkerProcessData.DoWork += BackgroundWorkerProcessData_DoWork;

            backgroundWorkerUpdateUI.WorkerSupportsCancellation = true;
            backgroundWorkerUpdateUI.DoWork += BackgroundWorkerUpdateUI_DoWork;

            backgroundWorkerReceiveData.WorkerSupportsCancellation = true;
            backgroundWorkerReceiveData.DoWork += BackgroundWorkerReceiveData_DoWork;

            backgroundWorkerSaveRawData.WorkerSupportsCancellation = true;
            backgroundWorkerSaveRawData.DoWork += backgroundWorkerSaveRawData_DoWork;

            hourTimer = new System.Timers.Timer(1000 * 60 * 60 * 1);
            hourTimer.Elapsed += new System.Timers.ElapsedEventHandler(HourTimer_TimesUp);
            hourTimer.AutoReset = true; //每到指定时间Elapsed事件是触发一次（false），还是一直触发（true）
            minuteTimer = new System.Timers.Timer(1000 * 60 * 5);
            minuteTimer.Elapsed += new System.Timers.ElapsedEventHandler(MinuteTimer_TimesUp);
            minuteTimer.AutoReset = false; //每到指定时间Elapsed事件是触发一次（false），还是一直触发（true）

            window = RaisedCosineWindow.Hann(WindowSize);

            this.ip = ip;
            this.localIP = localIP;
            this.deviceId = id;
            this.deviceType = deviceType;
            this.udpPort = port;
            this.chart1 = chart;
            this.textBoxLog = tb;
            this.basePath = path;
            this.database = database;
            this.isUpdateChart = false;
            this.vibrateChannels = new Dictionary<int, VibrateChannel>();

            db = redis.GetDatabase();
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

        private double CalculateCableForce(VibrateChannel channel, double frequency)
        {
            double result = 0;
            result = 4 * channel.Mass * channel.Length * channel.Length * frequency * frequency;
            return result;
        }

        public override string GetIP()
        {
            return this.ip;
        }

        public override void Start()
        {
            IPEndPoint iPEndPoint = new IPEndPoint(IPAddress.Parse(this.localIP), this.udpPort);
            udpClient = new UdpClient(iPEndPoint);
            backgroundWorkerUpdateUI.RunWorkerAsync();
            backgroundWorkerProcessData.RunWorkerAsync();
            backgroundWorkerReceiveData.RunWorkerAsync();
            //backgroundWorkerSaveRawData.RunWorkerAsync();
            hourTimer.Start();
            isSaveRawData = true;
            minuteTimer.Start();
        }

        public override void Stop()
        {
            udpClient.Close();
            backgroundWorkerUpdateUI.CancelAsync();
            backgroundWorkerProcessData.CancelAsync();
            backgroundWorkerReceiveData.CancelAsync();
            //backgroundWorkerSaveRawData.CancelAsync();
            hourTimer.Stop();
        }

        private void HourTimer_TimesUp(object sender, System.Timers.ElapsedEventArgs e)
        {
            isSaveRawData = true;
            minuteTimer.Start();

        }

        private void MinuteTimer_TimesUp(object sender, System.Timers.ElapsedEventArgs e)
        {
            isSaveRawData = false;

            string parent = Path.Combine(this.basePath, this.ip);
            if (!Directory.Exists(parent))
            {
                Directory.CreateDirectory(parent);
            }
            if (rawQueue.Count == 0)
            {
                return;
            }

            string line;
            string fileName = DateTime.Now.ToString("yyyy-MM-dd-HH-mm") + ".csv";
            string pathString = Path.Combine(parent, fileName);
            StreamWriter sw = new StreamWriter(pathString, true);

            for (int i = 0; i < rawQueue.Count; i++)
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
            uint fileSize = 49;

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

            while (true)
            {
                try
                {
                    float[] data;
                    bool success = uiQueue.TryDequeue(out data);

                    if (success)
                    {
                        //AccWave aw = new AccWave("5600001715001", "016", data);
                        ////AccWave aw = new AccWave("123", "acc", new float[] { 12.1F, 12.5F, 44.6F });
                        //byte[] result = string result = JsonConvert.SerializeObject(aw);
                        //udpClient.Send(result, result.Length);

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

            //IPAddress ip = IPAddress.Parse();
            //udpClient.Connect(IPAddress.Parse(ip), udpPort);
            //udpClient.Connect();
            byte[] start = { 0x00, 0x10, 0x00, 0xc8, 0x00, 0x01, 0x01, 0x01 };
            //this.udpClient.Send(start, start.Length);
            IPEndPoint RemoteIpEndPoint = new IPEndPoint(IPAddress.Any, 0);

            while (true)
            {
                try
                {
                    //IPEndPoint object will allow us to read datagrams sent from any source.
                    //IPEndPoint RemoteIpEndPoint = new IPEndPoint(IPAddress.Any, 0);

                    //udpClient.Connect()

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
                        sb.Append(data[i].ToString() + ",");
                        
                    }

                    sb.Remove(sb.Length - 1, 1);
                    sb.Append("\r\n");

                    if (isSaveRawData)
                    {
                        rawQueue.Enqueue(sb.ToString());
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
                        /* funny
                        float[,] channels = new float[8, WindowSize];

                        for (int i = 0; i < WindowSize; i++)
                        {
                            float[] line;

                            bool success = dataQueue.TryDequeue(out line);
                            if (success)
                            {
                                for (int j = 0; j < 8; j++)
                                {
                                    channels[j, i] = line[j];
                                }
                            }
                        }
                        */

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
                                    //sb.Append(line[j] + ",");
                                }
                                // sb.Remove(sb.Length - 1, 1);
                                // sb.Append("\r\n");
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

                //Thread.Sleep( 2000);
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
            //AppendLog(this.ip + "ProcessFrame Start ");
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
            
            //IPAddress remoteIp = IPAddress.Parse(this.spectrumIP);
            //int port = int.Parse(this.spectrumPort);
            //IPEndPoint remoteEndPoint = new IPEndPoint(remoteIp, this.spectrumPort);
            UdpClient udpClient = new UdpClient();
            Dictionary<RedisKey, RedisValue> pair = new Dictionary<RedisKey, RedisValue>();

            //IDatabase db_result = this.redis.GetDatabase(5);

            for (int i = 0; i < signal.Channels; i++)
            {
                //AppendLog(this.ip + "ProcessFrame calculate frame");
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
                    //AppendLog(this.ip + "ProcessFrame Send Spectrum");
                    VibrateChannel vc = vibrateChannels[i + 1];
                    float[] floatList = power[i].Select(x => (float)x).ToArray();
                    //AccWave awObject = new AccWave(vc.SensorId, "028", floatList);
                    //byte[] result = serializer.PackSingleObject(awObject);
                    //AppendLog(this.ip + " Frame Length: " + result.Length.ToString());
                    //udpClient.Send(result, result.Length, remoteEndPoint);
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

                content += frequency.ToString();
                content += ",";

                //AppendLog("Channel " + (i + 1).ToString() + " ");
                //if ((i + 1) == (int)numericUpDownChannel.Value)
                {
                    //AppendLog("Channel " + (i + 1).ToString() + " f1: " + frequency1.ToString() + " f2: " + frequency2.ToString() + " Fundamental frequency:" + frequency.ToString());

                    if (frequency != 0)
                    {
                        //string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        string str = "";
                        str += stamp + " ";
                        if (redis.IsConnected)
                        {

                        }
                        else
                        {
                            str += "redis server is not connected";
                            //lthis.AppendLog(str);
                            return;
                        }

                        if (vibrateChannels.ContainsKey(i + 1))
                        {
                            double force = 0;
                            VibrateChannel vc = vibrateChannels[i+1];
                            force = Math.Round(CalculateCableForce(vc, frequency) / 1000);

                            string key = vc.SensorId + "-012";
                            DataValue dv = new DataValue();
                            dv.SensorId = vc.SensorId;
                            dv.TimeStamp = stamp;
                            dv.ValueType = "012";
                            dv.Value = frequency;

                            //resultQueue.Enqueue(dv);

                            string result = JsonConvert.SerializeObject(dv);

                            pair[key] = result;

                            if (force != 0)
                            {
                                key = vc.SensorId + "-013";
                                DataValue dv1 = new DataValue();
                                dv1.SensorId = vc.SensorId;
                                dv1.TimeStamp = stamp;
                                dv1.ValueType = "013";
                                dv1.Value = force;

                                string result1 = JsonConvert.SerializeObject(dv1);
                                pair[key] = result;

                                //resultQueue.Enqueue(dv1);
                            }

                            AppendLog("Channel " + (i + 1).ToString() + " frequency: " + frequency.ToString() + " Cable Force: " + force.ToString());

                        }
                    }
                }
            }

            if (pair.Count > 0)
            {
                //db_result.StringSet(pair.ToArray());
                pair.Clear();
            }

            content = content.Remove(content.Length - 1);
            udpClient.Close();
            //AppendResult(content);

            //return;
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

            //AppendLog(this.ip+" ProcessFrame Finish ");
        }

        private void SaveToDatabase(string sensorId, string type, double value, string stamp)
        {
            SQLiteConnection conn = null;
            SQLiteCommand cmd;

            //string database = "Data Source = LongRui.db";

            try
            {
                conn = new SQLiteConnection(this.database);
                conn.Open();

                cmd = conn.CreateCommand();

                {
                    cmd.CommandText = "insert into data values('" + sensorId + "','" + stamp + "','" + type + "'," + value.ToString() + ")";
                }

                cmd.ExecuteNonQuery();

                conn.Close();
            }
            catch (Exception ex)
            {
                conn.Close();
                AppendLog(ex.Message);
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
        }

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
            return candidateFrequencies[minIndex];
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

        private void SearchFundamentalFrequency(double[] frequencies, int length, double precision)
        {
            List<List<double>> temp = new List<List<double>>();
            for (int i = 0; i < length - 1; i++)
            {
                for (int j = i + 1; j < length - 1; j++)
                {
                    List<double> tmpArr = new List<double>();
                    tmpArr.Add(frequencies[i]);
                    tmpArr.Add(frequencies[j]);
                    double delta = frequencies[j] - frequencies[i];
                    double tmpData = frequencies[j];
                    for (int k = j + 1; k < length - 1; k++)
                    {
                        if (Math.Abs(frequencies[k] - tmpData - delta) < precision)
                        {
                            //delta = frequencies[k] - tmpData;
                            tmpData = frequencies[k];
                            tmpArr.Add(frequencies[k]);
                        }
                    }
                    if (tmpArr.Count > 3)
                    {
                        string content = null;
                        content = "Delta: " + delta + "\r\n[";
                        foreach (double item in tmpArr)
                        {
                            content += item.ToString();
                            content += " ";
                            //AppendLog(
                        }
                        content += "]\r\n";
                        AppendLog(content);
                    }
                }
            }
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
