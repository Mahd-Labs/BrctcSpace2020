﻿using BrctcSpaceLibrary.DataModels;
using BrctcSpaceLibrary.Device;
using BrctcSpaceLibrary.Processes;
using Iot.Device.CpuTemperature;
using System;
using System.Collections.Generic;
using System.Device.Gpio;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BrctcSpaceLibrary.Systems
{
    public class FullSystem
    {
        private AccelerometerSystem AccelerometerSystem;
        private GyroscopeSystem GyroscopeSystem;
        private FileStream _gyroStream;

        public string AccelFileName { get => AccelerometerSystem.FileName; }

        public FullSystem()
        {
            DirectoryInfo dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            DirectoryInfo[] directories = dir.GetDirectories();
            int latestIteration = 0;
            foreach(var directory in directories)
            {
                string number = System.Text.RegularExpressions.Regex.Match(directory.Name, @"\d+").Value;

                Int32.TryParse(number, out int iteration);

                if (iteration > latestIteration)
                    latestIteration = iteration;
            }
            //use the latest found iteration and increment it as it will be the latest iteration
            string subDir = $"FullSystemSharedRTC_{++latestIteration}"; //should be used in final program

            dir.CreateSubdirectory(subDir);
            string accelFileName = Path.Combine(Directory.GetCurrentDirectory(), subDir, "Accelerometer.binary");
            string gyroFileName = Path.Combine(Directory.GetCurrentDirectory(), subDir, "Gyroscope.binary");

            AccelerometerSystem = new AccelerometerSystem(accelFileName);
            GyroscopeSystem = new GyroscopeSystem(gyroFileName);
        }

        /// <summary>
        /// Sets the amount of chunks to be written per file
        /// </summary>
        /// <param name="size"></param>
        public void SetChunkAmount(int size)
        {
            AccelerometerSystem.MaxChunksPerFile = size;
        }

        public void Run(CancellationToken token)
        {
            _gyroStream = new FileStream(GyroscopeSystem.FileName, FileMode.Create, FileAccess.Write);

            GyroscopeSystem.GyroStream = _gyroStream;

            Stopwatch stopwatch = new Stopwatch();

            stopwatch.Start();

            Task accelThread = Task.Run(() => { AccelerometerSystem.RunAccelerometer(token); });

            Task telemetryThread = Task.Run(() => Telemetry(token, AccelerometerSystem.SPS));

            Devices.GPIO.RegisterCallbackForPinValueChangedEvent(PinEventTypes.Rising, GyroscopeSystem.DataAquisitionCallback);

            bool loopBreakerProgramMaker = false;

            while (!loopBreakerProgramMaker)
            {
                if (token.IsCancellationRequested)
                {
                    Console.WriteLine("Time limit reached! Ending program...");
                    loopBreakerProgramMaker = true;
                    stopwatch.Stop();
                }
                else
                    Thread.SpinWait(50);
            }

            accelThread.Wait();
            telemetryThread.Wait();

            Devices.GPIO.UnregisterCallbackForPinValueChangedEvent(GyroscopeSystem.DataAquisitionCallback);
            _gyroStream.Dispose();

            Console.WriteLine($"FullSystemSharedRTC program ran for {stopwatch.Elapsed.TotalSeconds} seconds" +
                $" creating {AccelerometerSystem.AccelDatasetCounter} accelerometer datasets at " +
                $"{AccelerometerSystem.AccelDatasetCounter / stopwatch.Elapsed.TotalSeconds} datasets per second and" +
                $" {GyroscopeSystem.GyroDataSetCounter} gyroscope datasets at " +
                $"{GyroscopeSystem.GyroDataSetCounter / stopwatch.Elapsed.TotalSeconds} datasets per second");
        }

        private void Telemetry(CancellationToken token, int sps)
        {
            long indexTracker = 0; //tracks the current index of each line over multiple files
            int fileSent = 1;
            long currentSecond = 1;
            int sampleIndex = 0; // initialize to 0 but will increment to 1 right away. Compare using <=, rathter than <
            TemperatureModel temperature = new TemperatureModel();
            int prevSecond = 0;
            DateTime initialTime = DateTime.Now;
            bool isInitialDateTime = true;
            AccelerometerDataAnalysis processor = new AccelerometerDataAnalysis();

            int accelBytes = AccelerometerSystem.AccelBytes;
            int rtcBytes = AccelerometerSystem.Rtcbytes;
            int cpuBytes = AccelerometerSystem.CpuBytes;
            int accelSegmentLength = accelBytes + rtcBytes + cpuBytes;

            string header = $"ID,Timestamp,Second,Temp (F),SPS{processor.GenerateCsvHeaders()}";
            Task telemetryTask = Devices.UART.SerialSendAsync(header);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!AccelerometerSystem.FileQueue.IsEmpty)
                    {
                        Console.WriteLine($"Sending file # {fileSent}");
                        AccelerometerSystem.FileQueue.TryDequeue(out string fileName);
                        //logic to handle failed dequeue needs to be here
                        using (FileStream fs = new FileStream(fileName, FileMode.Open, FileAccess.Read))
                        {
                            byte[] bytes = new byte[accelSegmentLength];

                            while (fs.Read(bytes) != 0) //while data is in the stream, continue 
                            {
                                if (token.IsCancellationRequested)
                                {
                                    Console.WriteLine("Time's Up! Cancelling telemetry.");
                                    break;
                                }
                                Span<byte> data = bytes;
                                Span<byte> accelSegment = data.Slice(0, accelBytes);
                                Span<byte> rtcSegment = data.Slice(accelBytes, rtcBytes);
                                Span<byte> cpuSegment = data.Slice(accelBytes + rtcBytes, cpuBytes);

                                DateTime currentTime = new DateTime(BitConverter.ToInt64(rtcSegment));

                                sampleIndex++;

                                if (isInitialDateTime)
                                {
                                    prevSecond = currentTime.Second;
                                    initialTime = currentTime;
                                    isInitialDateTime = false;
                                }

                                //as long as the seconds match, get the data
                                if (prevSecond == currentTime.Second && sampleIndex <= sps)
                                {
                                    //add data for each second
                                    temperature.GetNextAverage(BitConverter.ToDouble(cpuSegment));
                                    processor.ProcessData(accelSegment);
                                }
                                else
                                {
                                    //perform analysis and send message on second change
                                    processor.PerformFFTAnalysis();

                                    //iterate second and append all data. Processor data should already have commas
                                    string message = $"{indexTracker++},{currentTime.ToString("HH:mm:ss")},{(currentTime - initialTime).TotalSeconds.ToString("F3")}," +
                                        $"{(int)temperature.AverageCPUTemp},{processor.SampleSize}{processor.X_Magnitudes}{processor.Y_Magnitudes}{processor.Z_Magnitudes}";

                                    try
                                    {
                                        if (!telemetryTask.IsCompleted)
                                        {
                                            telemetryTask.Wait();
                                        }
                                        telemetryTask = Devices.UART.SerialSendAsync(message);
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"Failed to send!\n{ex.Message}\n{ex.StackTrace}");
                                        Devices.UART.Dispose();
                                        Devices.UART = Devices.UART.GetUART(); // let's refresh the interface
                                    }

                                    //reset processor and temperature averge and begin new data set here
                                    processor.Reset();
                                    temperature.Reset();
                                    temperature.GetNextAverage(BitConverter.ToDouble(cpuSegment));
                                    processor.ProcessData(accelSegment);
                                    sampleIndex = 1; //start at one since we already have added our next first datapoint
                                }

                                prevSecond = currentTime.Second;

                                data.Clear(); // since we are reusing this array, clear the values for integrity                                  
                            }
                        }
                        Console.WriteLine($"Finished sending file #{fileSent++} at {indexTracker} total lines transmitted!");
                    }
                    else
                    {
                        Thread.Sleep(1000);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    Console.WriteLine(ex.StackTrace);
                }
                finally
                {
                    Devices.UART.Dispose();
                    Devices.UART = Devices.UART.GetUART(); // let's refresh the interface
                }
            }
        }

    }
}
