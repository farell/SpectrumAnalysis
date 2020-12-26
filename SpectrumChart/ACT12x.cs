using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SpectrumChart
{
    public class ACT12x
    {
        public virtual void Start()
        {
        }

        public virtual void Stop()
        {
        }

        public virtual void SetUpdateChart(bool update) { }

    }

    class VibrateChannel
    {
        public string SensorId;
        public int ChannelNo;
        public double Length;
        public double Mass;

        public VibrateChannel(string sensorId,int channelNo,double length,double mass)
        {
            this.SensorId = sensorId;
            this.ChannelNo = channelNo;
            this.Length = length;
            this.Mass = mass;
        }
    }
}
