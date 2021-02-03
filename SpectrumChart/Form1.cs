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
using Newtonsoft.Json;
using System.Data.SQLite;
using System.Globalization;
using System.Data.SqlClient;
using StackExchange.Redis;
using System.Configuration;

namespace SpectrumChart
{
    

    public partial class Form1 : Form
    {
        private ConcurrentQueue<DataValue> dataQueue;
        private BackgroundWorker backgroundWorkerSaveData;
        private float[] signalBuffer;
        private IWindow window;
        private const int WindowSize = 512;
        private ACT12x vact;
        private string database = "DataSource = Vibrate.db";
        private ConnectionMultiplexer redis;

        private bool saveDataSuccess;
        private bool isStarted;

        public string databaseIp;
        public string databaseUser;
        public string databasePwd;
        public string databaseName;
        public string databaseTable;

        private Dictionary<string,ACT12x> deviceList;

        public Form1()
        {
            InitializeComponent();
            redis = ConnectionMultiplexer.Connect("localhost,abortConnect=false");

            deviceList = new Dictionary<string, ACT12x>();

            for (int x = 0; x < checkedListBoxChannel.Items.Count; x++)
            {
                this.checkedListBoxChannel.SetItemChecked(x, false);
            }

            dataQueue = new ConcurrentQueue<DataValue>();

            backgroundWorkerSaveData = new BackgroundWorker();
            signalBuffer = new float[WindowSize];

            backgroundWorkerSaveData.WorkerSupportsCancellation = true;
            backgroundWorkerSaveData.DoWork += BackgroundWorkerSaveData_DoWork;

            window = RaisedCosineWindow.Hann(WindowSize);

            LoadDevices();

            vact = null;
            saveDataSuccess = false;
            isStarted = false;
            ToolStripMenuItemStart.Enabled = true;
            ToolStripMenuItemStop.Enabled = false;
        }


        private void LoadDevices()
        {
            this.deviceList.Clear();
            using (SQLiteConnection connection = new SQLiteConnection(database))
            {
                connection.Open();
                string strainStatement = "select RemoteIP,LocalPort,DeviceId,Type,Desc,Path,IsCalculateForce,Threshold,LocalIP from SensorInfo";
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
                        double threshold = reader.GetDouble(7);
                        string loclaIP = reader.GetString(8);

                        string[] itemString = { description, type, deviceId, remoteIP, localPort.ToString(),path ,loclaIP};
                        ListViewItem item = new ListViewItem(itemString);
                        item.Checked = true;

                        listView1.Items.Add(item);

                        ACT12x device = null;

                        if(type == "ACT1228CableForce")
                        {
                            device = new ACT1228CableForce(deviceId, remoteIP, loclaIP, localPort, chart1, type, path, this.database, textBoxLog, threshold, redis);
                        }else if(type == "ACT12816Vibrate")
                        {
                            device = new ACT12816Vibrate(deviceId, remoteIP, loclaIP, localPort, chart1, type, path, this.database, textBoxLog, threshold, redis);
                        }
                        else if (type == "ACT1228EarthQuake")
                        {
                            device = new ACT1228EarthQuake(deviceId, remoteIP, loclaIP, localPort, chart1, type, path, this.database, textBoxLog, threshold, redis);
                        }
                        else if (type == "ACT1228Vibrate")
                        {
                            device = new ACT1228Vibrate(deviceId, remoteIP, loclaIP, localPort, chart1, type, path, this.database, textBoxLog, threshold, redis);
                        }
                        else if (type == "ACT1228CableForceV4")
                        {
                            device = new ACT1228CableForceV4(deviceId, remoteIP, loclaIP, localPort, chart1, type, path, this.database, textBoxLog, threshold, redis);
                        }
                        else if(type == "ACT1228VibrateV4")
                        {
                            device = new ACT1228VibrateV4(deviceId, remoteIP, loclaIP, localPort, chart1, type, path, this.database, textBoxLog, threshold, redis);
                        }else
                        {

                        }
                        
                        if (device != null)
                        {
                            this.deviceList.Add(deviceId, device);
                        }
                    }
                }

                strainStatement = "select ip,user,password,database,tableName from dbconfig";
                SQLiteCommand command3 = new SQLiteCommand(strainStatement, connection);
                using (SQLiteDataReader reader2 = command3.ExecuteReader())
                {
                    while (reader2.Read())
                    {
                        databaseIp = reader2.GetString(0);
                        databaseUser = reader2.GetString(1);
                        databasePwd = reader2.GetString(2);
                        databaseName = reader2.GetString(3);
                        databaseTable = reader2.GetString(4);
                    }
                }

                connection.Close();
            }
        }

        private void listView1_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (listView1.SelectedIndices != null && listView1.SelectedIndices.Count > 0)
            {
                ListView.SelectedIndexCollection c = listView1.SelectedIndices;

                List<string> checkedIndices = new List<string>();
                foreach (ListViewItem item in listView1.CheckedItems)
                {
                    checkedIndices.Add(item.SubItems[3].Text);
                }
                string tag = listView1.Items[c[0]].SubItems[3].Text;
                if (!checkedIndices.Contains(tag))
                {
                    return;
                }
                
                chart1.Titles[0].Text = listView1.Items[c[0]].SubItems[0].Text;

                string key = listView1.Items[c[0]].SubItems[2].Text;

                if (deviceList.ContainsKey(key))
                {
                    if (vact == null)
                    {
                        vact = deviceList[key];
                        vact.SetUpdateChart(true);
                    }
                    else
                    {
                        vact.SetUpdateChart(false);
                        vact = deviceList[key];
                        vact.SetUpdateChart(true);
                    }
                }

            }
        }

        public void Start()
        {
            ToolStripMenuItemStart.Enabled = false;
            ToolStripMenuItemStop.Enabled = true;
            //buttonStart.Enabled = false;
            //buttonStop.Enabled = true;
            isStarted = true;
            List<string> checkedIndices = new List<string>();
            foreach (ListViewItem item in listView1.CheckedItems)
            {
                checkedIndices.Add(item.SubItems[3].Text);
            }
            foreach (ACT12x va in deviceList.Values)
            {
                string ip = va.GetIP();
                if (checkedIndices.Contains(ip))
                {
                    va.Start();
                }
            }
        }

        public void Stop()
        {
            ToolStripMenuItemStart.Enabled = true;
            ToolStripMenuItemStop.Enabled = false;
            //buttonStart.Enabled = true;
            //buttonStop.Enabled = false;
            isStarted = false;
            List<string> checkedIndices = new List<string>();
            foreach (ListViewItem item in listView1.CheckedItems)
            {
                checkedIndices.Add(item.SubItems[3].Text);
            }
            foreach (ACT12x va in deviceList.Values)
            {
                string ip = va.GetIP();
                if (checkedIndices.Contains(ip))
                {
                    va.Stop();
                }
            }
        }

        private void buttonStart_Click(object sender, EventArgs e)
        {
            Start();
        }

        private void buttonStop_Click(object sender, EventArgs e)
        {
            Stop();
        }

        private void checkedListBoxChannel_SelectedIndexChanged(object sender, EventArgs e)
        {
            int index = checkedListBoxChannel.SelectedIndex;
            if(chart1.Series[index] !=null && chart1.Series[index + 16] != null)
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
                if (isStarted)
                {
                    this.Stop();
                }
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

        private void buttonTest_Click(object sender, EventArgs e)
        {
            DatabaseConfig dlg = new DatabaseConfig(this);
            dlg.ShowDialog();
            //semaphore.Release();
            //Director("1");
        }

        public void UpdateDatabaseSetting(string ip, string user, string pwd, string database, string table)
        {
            this.databaseIp = ip;
            this.databaseUser = user;
            this.databasePwd = pwd;
            this.databaseName = database;
            this.databaseTable = table;

            using (SQLiteConnection connection = new SQLiteConnection(this.database))
            {
                connection.Open();
                string strainStatement = "delete from dbconfig";
                SQLiteCommand command = new SQLiteCommand(strainStatement, connection);
                command.ExecuteNonQuery();

                strainStatement = "insert into dbconfig values('" + databaseIp + "','" + databaseUser + "','" + databasePwd + "','" + databaseName + "','" + databaseTable + "')";
                command = new SQLiteCommand(strainStatement, connection);
                command.ExecuteNonQuery();
                connection.Close();
            }
        }

        private void AppendLog(string message)
        {
            //if (textBoxLog.InvokeRequired)
            //{
            //    textBoxLog.BeginInvoke(new MethodInvoker(() =>
            //    {
            //        textBoxLog.AppendText(message + " \r\n");
            //    }));
            //}
            //else
            //{
            //    textBoxLog.AppendText(message + " \r\n");
            //}
        }

        private void BackgroundWorkerSaveData_DoWork(object sender, DoWorkEventArgs e)
        {
            BackgroundWorker bgWorker = sender as BackgroundWorker;

            DataTable dt = GetTableSchema();

            while (true)
            {
                try
                {
                    int dataCount = dataQueue.Count;

                    if (dataCount > 100)
                    {
                        if (saveDataSuccess)
                        {
                            dt.Clear();
                        }

                        for (int i = 0; i < dataCount; i++)
                        {
                            DataValue dv;
                            bool success = dataQueue.TryDequeue(out dv);
                            if (success)
                            {
                                DataRow row = dt.NewRow();
                                row[0] = dv.SensorId;
                                row[1] = DateTime.Parse(dv.TimeStamp);
                                row[2] = dv.ValueType;
                                row[3] = dv.Value;
                                dt.Rows.Add(row);
                            }
                        }
                        InsertData(dt, "data");
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

        private DataTable GetTableSchema()
        {
            DataTable dt = new DataTable();
            dt.Columns.AddRange(new DataColumn[] {
                //new DataColumn("ID",typeof(int)),
                new DataColumn("SensorId",typeof(string)),
                new DataColumn("Stamp",typeof(System.DateTime)),
                new DataColumn("Type",typeof(string)),
                new DataColumn("Value",typeof(Single))
            });
            return dt;
        }

        private void InsertData(DataTable dt, string tableName)
        {
            string connectionString = "Data Source = " + databaseIp + ";Network Library = DBMSSOCN;Initial Catalog = " + databaseName + ";User ID = " + databaseUser + ";Password = " + databasePwd;

            //Stopwatch sw = new Stopwatch();

            try
            {
                using (SqlConnection conn = new SqlConnection(connectionString))
                {
                    SqlBulkCopy bulkCopy = new SqlBulkCopy(conn);
                    bulkCopy.DestinationTableName = tableName;
                    bulkCopy.BatchSize = dt.Rows.Count;
                    //bulkCopy.BulkCopyTimeout
                    conn.Open();
                    //sw.Start();

                    if (dt != null && dt.Rows.Count != 0)
                    {
                        bulkCopy.WriteToServer(dt);
                        //sw.Stop();
                    }
                    //textBoxLog.AppendText(string.Format("插入{0}条记录共花费{1}毫秒，{2}分钟", dt.Rows.Count, sw.ElapsedMilliseconds, sw.ElapsedMilliseconds / 1000 / 60));
                    conn.Close();
                    saveDataSuccess = true;
                }
            }
            catch (Exception ex)
            {
                saveDataSuccess = false;
                //if (textBoxLog.InvokeRequired)
                //{
                //    textBoxLog.BeginInvoke(new MethodInvoker(() =>
                //    {
                //        textBoxLog.AppendText(ex.Message + "\r\n");
                //    }));
                //}
                //else
                //{
                //    textBoxLog.AppendText(ex.Message + "\r\n");
                //}
            }
        }

        private void ToolStripMenuItemDatabaseConfig_Click(object sender, EventArgs e)
        {
            DatabaseConfig dlg = new DatabaseConfig(this);
            dlg.ShowDialog();
        }

        private void ToolStripMenuItemStart_Click(object sender, EventArgs e)
        {
            Start();
        }

        private void ToolStripMenuItemStop_Click(object sender, EventArgs e)
        {
            Stop();
        }
    }

    public class DataValue
    {
        public string SensorId { get; set; }
        public string TimeStamp { get; set; }
        public string ValueType { get; set; }
        public double Value { get; set; }
    }
}
