using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.SqlClient;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace SpectrumChart
{
    public partial class DatabaseConfig : Form
    {
        private Form1 form;
        public DatabaseConfig(Form1 form)
        {
            InitializeComponent();
            this.form = form;
            this.textBoxIp.Text = form.databaseIp;
            this.textBoxDatabase.Text = form.databaseName;
            this.textBoxPwd.Text = form.databasePwd;
            this.textBoxTable.Text = form.databaseTable;
            this.textBoxUser.Text = form.databaseUser;
        }

        private void buttonTestConnection_Click(object sender, EventArgs e)
        {
            string connectionString = "Data Source = "+textBoxIp.Text+";Network Library = DBMSSOCN;Initial Catalog = "+textBoxDatabase.Text+";User ID = "+textBoxUser.Text+";Password = "+textBoxPwd.Text;
            try
            {
                using (SqlConnection conn = new SqlConnection(connectionString))
                {
                    conn.Open();
                    MessageBox.Show("连接成功！");
                    conn.Close();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void buttonOk_Click(object sender, EventArgs e)
        {
            this.form.UpdateDatabaseSetting(textBoxIp.Text, textBoxUser.Text, textBoxPwd.Text, textBoxDatabase.Text, textBoxTable.Text);
            this.Close();
        }

        private void buttonCancel_Click(object sender, EventArgs e)
        {
            this.Close();
        }
    }
}
