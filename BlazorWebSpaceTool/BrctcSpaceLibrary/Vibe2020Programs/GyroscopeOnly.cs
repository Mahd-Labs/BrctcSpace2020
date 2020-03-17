﻿using BrctcSpaceLibrary.Device;
using System;
using System.Device.Gpio;
using System.Device.Spi;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace BrctcSpaceLibrary.Vibe2020Programs
{
    public class GyroscopeOnly : ISingleDevice
    {
        private Gyroscope _gyroscopeDevice;
        private GpioController _gpio;
        private const int DR_PIN = 13; //physical pin scheme;
        private int _segmentLength = 20;
        private string _fileName;
        private Memory<byte> _gyroSegment;

        //Only used by outside methods. Should always be the same as the internal filename
        public static string FileName { get => Path.Combine(Directory.GetCurrentDirectory(), "GyroscopeOnly.binary"); }

        // Keeps track of the amount of data sets for our test
        private long _datasetCounter = 0;

        public GyroscopeOnly()
        {
            var settings = new SpiConnectionSettings(0, 0) { Mode = SpiMode.Mode3, ClockFrequency = 900000 };

            _gyroscopeDevice = new Gyroscope(settings);

            _fileName = Path.Combine(Directory.GetCurrentDirectory(), "GyroscopeOnly.binary");

            _gyroSegment = new Memory<byte>(new byte[_segmentLength]);

            _gyroscopeDevice.RegisterWrite((byte)Register.MSC_CTRL, 0xC1);
            _gyroscopeDevice.RegisterWrite((byte)Register.FLTR_CTRL, 0x500);
            _gyroscopeDevice.RegisterWrite((byte)Register.DEC_RATE, 0);

            _gpio = new GpioController(PinNumberingScheme.Board);
            _gpio.OpenPin(DR_PIN, PinMode.Input);
            _gpio.RegisterCallbackForPinValueChangedEvent(DR_PIN, PinEventTypes.Rising, DataAquisitionCallback);
        }

        public void Run(double timeLimit, System.Threading.CancellationToken token)
        {
            Stopwatch stopwatch = new Stopwatch();


            stopwatch.Start();


            while (stopwatch.Elapsed.TotalMinutes < timeLimit && !token.IsCancellationRequested)
            {
                //allow interrupt to perform reads
            }

            stopwatch.Stop();

            Console.WriteLine($"GyroscopeOnly program ran for {stopwatch.Elapsed.TotalSeconds} seconds" +
                $" creating {_datasetCounter} datasets at {_datasetCounter / stopwatch.Elapsed.TotalSeconds} datasets per second");
        }

        private void DataAquisitionCallback(object sender, PinValueChangedEventArgs pinValueChangedEventArgs)
        {
            using (FileStream fs = new FileStream(_fileName, FileMode.Create, FileAccess.Write))
            {
                using (MemoryStream stream = new MemoryStream())
                {
                    _gyroscopeDevice.AcquireData(_gyroSegment.Span);
                    stream.Write(_gyroSegment.Span);

                    _datasetCounter++;

                    _gyroSegment.Span.Clear();

                    stream.WriteTo(fs);
                    fs.Flush();
                    stream.Position = 0;
                }
            }
        }

        public long GetDataSetCount()
        {
            return _datasetCounter;
        }

        public string GetFileName()
        {
            return _fileName;
        }

        public int GetSegmentLength()
        {
            return _segmentLength;
        }

        ~GyroscopeOnly()
        {
            _gyroscopeDevice.Dispose();
            _gpio.UnregisterCallbackForPinValueChangedEvent(DR_PIN, DataAquisitionCallback);
            _gpio.ClosePin(DR_PIN);
            _gpio.Dispose();
        }
    }
}