using System;
using System.Diagnostics;
using System.IO;

class Program
{
    static void Main(string[] args)
    {
        string rawFile = "test_48k.raw";
        
        // Generate a 1-second 48kHz sine wave raw file for testing
        using (var fs = new FileStream(rawFile, FileMode.Create))
        using (var bw = new BinaryWriter(fs))
        {
            for (int i = 0; i < 48000; i++)
            {
                short sample = (short)(Math.Sin(2 * Math.PI * 440 * i / 48000) * 10000);
                bw.Write(sample);
            }
        }
        Console.WriteLine($"Created {rawFile}");

        string mp3Path = "test_output.mp3";
        try 
        {
             var convertStart = new ProcessStartInfo("/usr/bin/ffmpeg")
             {
                 RedirectStandardOutput = true,
                 RedirectStandardError = true,
                 UseShellExecute = false,
                 CreateNoWindow = true
             };
             
             // Input: Raw 48k
             convertStart.ArgumentList.Add("-f"); convertStart.ArgumentList.Add("s16le");
             convertStart.ArgumentList.Add("-ar"); convertStart.ArgumentList.Add("48000");
             convertStart.ArgumentList.Add("-ac"); convertStart.ArgumentList.Add("1");
             convertStart.ArgumentList.Add("-i"); convertStart.ArgumentList.Add(rawFile);
             
             // Output: MP3 VBR
             convertStart.ArgumentList.Add("-codec:a"); convertStart.ArgumentList.Add("libmp3lame");
             convertStart.ArgumentList.Add("-qscale:a"); convertStart.ArgumentList.Add("4");
             convertStart.ArgumentList.Add(mp3Path);
             convertStart.ArgumentList.Add("-y");

             Console.WriteLine("Running ffmpeg...");
             using (var proc = Process.Start(convertStart))
             {
                 var stderr = proc.StandardError.ReadToEnd();
                 proc.WaitForExit();
                 if (proc.ExitCode != 0)
                 {
                     Console.WriteLine($"FFmpeg failed: {stderr}");
                 }
                 else
                 {
                     Console.WriteLine("FFmpeg success.");
                 }
             }

             if (File.Exists(mp3Path))
             {
                 Console.WriteLine($"MP3 created: {new FileInfo(mp3Path).Length} bytes");
             }
             else
             {
                 Console.WriteLine("MP3 file missing.");
             }
        }
        catch (Exception ex)
        {
             Console.WriteLine($"Exception: {ex.Message}");
        }
    }
}
