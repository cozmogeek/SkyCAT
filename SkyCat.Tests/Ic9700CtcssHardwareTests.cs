using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using Microsoft.Extensions.Logging;
using skycatd;
using SkyCat;
using Xunit.Abstractions;

namespace SkyCat.Tests
{
  // manual, hardware-in-the-loop harness for the CTCSS commands added to SkyCat. it drives the real
  // rigctld dispatch (CommandInterpreter -> CatCommandSender) against a live IC-9700 and verifies each
  // command by reading the value back over CI-V. it never keys the transmitter, so nothing is radiated.
  //
  // skipped unless SKYCAT_RADIO_PORT is set. to run:
  //   SKYCAT_RADIO_PORT=COM7 dotnet test --filter FullyQualifiedName~Ic9700CtcssHardwareTests
  // optional: SKYCAT_RADIO_BAUD (defaults to the IC-9700.json baud). the tone/function are restored
  // to their original values when the test finishes.
  public class Ic9700CtcssHardwareTests
  {
    private const byte Radio = 0xA2;
    private const byte Ctrl = 0xE0;

    private readonly ITestOutputHelper Output;
    public Ic9700CtcssHardwareTests(ITestOutputHelper output) => Output = output;

    private static string? Port => Environment.GetEnvironmentVariable("SKYCAT_RADIO_PORT");
    private static int? Baud =>
      int.TryParse(Environment.GetEnvironmentVariable("SKYCAT_RADIO_BAUD"), out var b) ? b : null;

    // marks a test that needs a live radio: skipped unless SKYCAT_RADIO_PORT is set at launch
    private sealed class HardwareFactAttribute : FactAttribute
    {
      public HardwareFactAttribute()
      {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SKYCAT_RADIO_PORT")))
          Skip = "set SKYCAT_RADIO_PORT (e.g. COM7) to run the IC-9700 hardware harness";
      }
    }




    //----------------------------------------------------------------------------------------------
    //                                          tests
    //----------------------------------------------------------------------------------------------
    [HardwareFact]
    public void SimplexSetsAndReadsBackCtcssTone()
    {
      RunCycle(OperatingMode.Simplex, "S 0 VFO");
    }

    [HardwareFact]
    public void DuplexSetsAndReadsBackCtcssTone()
    {
      RunCycle(OperatingMode.Duplex, "S 1 Sub");
    }

    private void RunCycle(OperatingMode mode, string setupCommand)
    {
      bool duplex = mode == OperatingMode.Duplex;
      var interp = new CommandInterpreter(new Options { Model = "IC-9700", RigFile = Port!, SerialSpeed = Baud },
        new TestLogger(Output));
      var port = interp.CommandSender.SerialPort;

      Output.WriteLine($"=== {mode} on {Port} @ {(Baud?.ToString() ?? "default")} baud ===");
      port.Open();
      try
      {
        Assert.Equal("RPRT 0", interp.Execute(setupCommand));

        // remember the radio's current tone and encode state, restore it at the end
        int originalTone = ReadTone(port, duplex);
        bool originalOn = ReadToneFunc(port, duplex);
        Output.WriteLine($"original: tone={originalTone / 10.0:0.0} Hz, encode={(originalOn ? "ON" : "OFF")}");

        try
        {
          // set encode tone to 67.0 Hz and read it back
          AssertOk(interp, "C 670");
          Assert.Equal(670, ReadTone(port, duplex));

          // a value with digits in every position proves the BCD encoding (141.3 Hz)
          AssertOk(interp, "C 1413");
          Assert.Equal(1413, ReadTone(port, duplex));

          // the SO-50 arming tone
          AssertOk(interp, "C 744");
          Assert.Equal(744, ReadTone(port, duplex));

          // enable / disable the encode (repeater tone) function
          AssertOk(interp, "U TONE 1");
          Assert.True(ReadToneFunc(port, duplex));
          AssertOk(interp, "U TONE 0");
          Assert.False(ReadToneFunc(port, duplex));
        }
        finally
        {
          WriteTone(port, duplex, originalTone);
          WriteToneFunc(port, duplex, originalOn);
          Output.WriteLine($"restored: tone={originalTone / 10.0:0.0} Hz, encode={(originalOn ? "ON" : "OFF")}");
        }
      }
      finally
      {
        port.Close();
      }
    }

    private void AssertOk(CommandInterpreter interp, string command)
    {
      string reply = interp.Execute(command);
      Output.WriteLine($"{command,-12} -> {reply}");
      Assert.Equal("RPRT 0", reply);
    }




    //----------------------------------------------------------------------------------------------
    //                              raw CI-V read-back / restore helpers
    //----------------------------------------------------------------------------------------------
    // read the repeater (encode) tone frequency in tenths of Hz
    private int ReadTone(SerialPort port, bool duplex)
    {
      if (duplex) Transact(port, Frame(0x07, 0xD2, 0x01)); // select sub (TX band in sat mode)
      var reply = Transact(port, Frame(0x1B, 0x00));       // read repeater tone frequency
      // reply: FE FE E0 A2 1B 00 <b0 b1 b2> FD  -> 3 BCD bytes before the trailing FD
      Assert.True(reply.Length >= 10, $"short tone reply: {BitConverter.ToString(reply)}");
      int n = reply.Length;
      return Bcd(reply[n - 4]) * 10000 + Bcd(reply[n - 3]) * 100 + Bcd(reply[n - 2]);
    }

    // read the repeater (encode) tone function on/off state
    private bool ReadToneFunc(SerialPort port, bool duplex)
    {
      if (duplex) Transact(port, Frame(0x07, 0xD2, 0x01));
      var reply = Transact(port, Frame(0x16, 0x42));       // read repeater tone function
      // reply: FE FE E0 A2 16 42 <vv> FD
      Assert.True(reply.Length >= 8, $"short function reply: {BitConverter.ToString(reply)}");
      return reply[reply.Length - 2] == 0x01;
    }

    private void WriteTone(SerialPort port, bool duplex, int tenthsHz)
    {
      if (duplex) Transact(port, Frame(0x07, 0xD2, 0x01));
      string digits = (tenthsHz % 1000000).ToString("D6");
      Transact(port, Frame(0x1B, 0x00, ToBcd(digits[0], digits[1]), ToBcd(digits[2], digits[3]), ToBcd(digits[4], digits[5])));
    }

    private void WriteToneFunc(SerialPort port, bool duplex, bool on)
    {
      if (duplex) Transact(port, Frame(0x07, 0xD2, 0x01));
      Transact(port, Frame(0x16, 0x42, (byte)(on ? 0x01 : 0x00)));
    }

    // send one CI-V frame and return the radio's reply frame, asserting the reply is positive:
    // a well-formed frame from the radio (FE FE E0 A2 ... FD) that is not the NAK (... FA FD)
    private byte[] Transact(SerialPort port, byte[] frame)
    {
      DumpInput(port);
      port.Write(frame, 0, frame.Length);
      var reply = ReadFrame(port);
      Output.WriteLine($"  sent {BitConverter.ToString(frame)}   recv {BitConverter.ToString(reply)}");

      string hex = BitConverter.ToString(reply);
      Assert.True(reply.Length >= 6 && reply[0] == 0xFE && reply[1] == 0xFE
        && reply[2] == Ctrl && reply[3] == Radio && reply[^1] == 0xFD, $"malformed reply: {hex}");
      Assert.False(reply[4] == 0xFA, $"radio rejected the command (NAK): {hex}");
      return reply;
    }

    // read bytes until the CI-V end marker (0xFD) or a timeout
    private static byte[] ReadFrame(SerialPort port, int timeoutMs = 1500)
    {
      port.ReadTimeout = timeoutMs;
      var buffer = new List<byte>();
      var stopwatch = Stopwatch.StartNew();
      try
      {
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
          int b = port.ReadByte();
          buffer.Add((byte)b);
          if (b == 0xFD) break;
        }
      }
      catch (TimeoutException) { }
      return buffer.ToArray();
    }

    private static void DumpInput(SerialPort port)
    {
      if (port.BytesToRead > 0) port.ReadExisting();
    }

    private static byte[] Frame(params byte[] payload)
    {
      var frame = new byte[payload.Length + 5];
      frame[0] = 0xFE; frame[1] = 0xFE; frame[2] = Radio; frame[3] = Ctrl;
      Array.Copy(payload, 0, frame, 4, payload.Length);
      frame[^1] = 0xFD;
      return frame;
    }

    private static int Bcd(byte b) => (b >> 4) * 10 + (b & 0x0F);
    private static byte ToBcd(char tens, char ones) => (byte)(((tens - '0') << 4) | (ones - '0'));




    //----------------------------------------------------------------------------------------------
    //                                     logging adapter
    //----------------------------------------------------------------------------------------------
    private sealed class TestLogger : ILogger
    {
      private readonly ITestOutputHelper output;
      public TestLogger(ITestOutputHelper output) => this.output = output;

      public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
      public bool IsEnabled(LogLevel logLevel) => true;

      public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
      {
        output.WriteLine(formatter(state, exception));
        if (exception != null) output.WriteLine(exception.ToString());
      }

      private sealed class NullScope : IDisposable
      {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
      }
    }
  }
}
