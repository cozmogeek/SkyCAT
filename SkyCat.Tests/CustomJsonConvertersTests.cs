using System.Collections.Generic;
using Newtonsoft.Json;
using SkyCat;

namespace SkyCat.Tests
{
  // the hand-written hex converters underpin every rig file, so their edge cases get direct coverage
  public class CustomJsonConvertersTests
  {
    private static readonly HexStringToNullableByteArrayConverter NullableArray = new();
    private static readonly HexStringToByteConverter SingleByte = new();
    private static readonly DictionaryStringToByteArrayConverter Dictionary = new();




    //----------------------------------------------------------------------------------------------
    //                            nullable byte array (command / reply bytes)
    //----------------------------------------------------------------------------------------------
    [Fact]
    public void ParsesArrayWithNullPlaceholder()
    {
      var result = JsonConvert.DeserializeObject<byte?[]>("""["1B", null, "FD"]""", NullableArray);
      Assert.Equal(new byte?[] { 0x1B, null, 0xFD }, result);
    }

    [Fact]
    public void ParsesArrayWith0xPrefix()
    {
      var result = JsonConvert.DeserializeObject<byte?[]>("""["0x1B", "0xFD"]""", NullableArray);
      Assert.Equal(new byte?[] { 0x1B, 0xFD }, result);
    }

    [Fact]
    public void ParsesConcatenatedHexString()
    {
      var result = JsonConvert.DeserializeObject<byte?[]>("\"FEFE\"", NullableArray);
      Assert.Equal(new byte?[] { 0xFE, 0xFE }, result);
    }

    [Fact]
    public void PadsOddLengthHexString()
    {
      var result = JsonConvert.DeserializeObject<byte?[]>("\"F\"", NullableArray);
      Assert.Equal(new byte?[] { 0xF0 }, result);
    }

    [Fact]
    public void ThrowsOnInvalidHexElement()
    {
      Assert.Throws<JsonException>(() =>
        JsonConvert.DeserializeObject<byte?[]>("""["ZZ"]""", NullableArray));
    }

    [Fact]
    public void SerializesNullableArrayBackToHexStrings()
    {
      var json = JsonConvert.SerializeObject(new byte?[] { 0x1B, null, 0xFD }, NullableArray);
      Assert.Equal("""["0x1B",null,"0xFD"]""", json);
    }




    //----------------------------------------------------------------------------------------------
    //                                       single byte
    //----------------------------------------------------------------------------------------------
    [Fact]
    public void ParsesSingleHexByte()
    {
      var result = JsonConvert.DeserializeObject<byte>("\"0x42\"", SingleByte);
      Assert.Equal(0x42, result);
    }

    [Fact]
    public void SingleByteThrowsOnInvalidValue()
    {
      Assert.Throws<JsonException>(() =>
        JsonConvert.DeserializeObject<byte>("\"GG\"", SingleByte));
    }




    //----------------------------------------------------------------------------------------------
    //                              string -> byte[] dictionary (enum values)
    //----------------------------------------------------------------------------------------------
    [Fact]
    public void ParsesEnumValueDictionary()
    {
      var dict = JsonConvert.DeserializeObject<Dictionary<string, byte[]>>(
        """{ "FM": ["34"], "USB": ["32"] }""", Dictionary);

      Assert.NotNull(dict);
      Assert.Equal(new byte[] { 0x34 }, dict!["FM"]);
      Assert.Equal(new byte[] { 0x32 }, dict["USB"]);
    }
  }
}
