using Parrhesia.Audio.Buffers;

namespace Parrhesia.Audio.Tests.Buffers;

public class SampleRingTests
{
    [Fact]
    public void Capacity_MustBePowerOfTwo()
    {
        Assert.Throws<ArgumentException>(() => new SampleRing(100));
        _ = new SampleRing(64);
    }

    [Fact]
    public void WriteRead_PreservesOrder()
    {
        var ring = new SampleRing(8);
        var written = ring.Write(new float[] { 1, 2, 3, 4 });

        Assert.Equal(4, written);
        Assert.Equal(4, ring.Available);

        var read = new float[4];
        Assert.Equal(4, ring.Read(read));
        Assert.Equal(new float[] { 1, 2, 3, 4 }, read);
        Assert.Equal(0, ring.Available);
    }

    [Fact]
    public void Read_FillsShortageWithSilence_AndCountsUnderrun()
    {
        var ring = new SampleRing(8);
        ring.Write(new float[] { 1, 2 });

        var read = new float[5];
        var actual = ring.Read(read);

        Assert.Equal(2, actual);
        Assert.Equal(new float[] { 1, 2, 0, 0, 0 }, read);
        Assert.Equal(3, ring.UnderrunSamples);
    }

    [Fact]
    public void Write_OverflowsWhenFull_AndDropsNewSamples()
    {
        var ring = new SampleRing(4);
        ring.Write(new float[] { 1, 2, 3, 4 });

        var accepted = ring.Write(new float[] { 5, 6 });

        Assert.Equal(0, accepted);
        Assert.Equal(2, ring.OverflowSamples);
        Assert.Equal(4, ring.Available);
    }

    [Fact]
    public void Write_TakesPartialSpace_WhenNotCompletelyFull()
    {
        var ring = new SampleRing(4);
        ring.Write(new float[] { 1, 2, 3 });

        var accepted = ring.Write(new float[] { 4, 5, 6 });

        Assert.Equal(1, accepted);
        Assert.Equal(2, ring.OverflowSamples);
        Assert.Equal(4, ring.Available);
    }

    [Fact]
    public void WrapAround_KeepsFifoOrder()
    {
        var ring = new SampleRing(8);
        var sink = new float[6];

        // Три круга: пишем и читаем попеременно через границу буфера.
        ring.Write(new float[] { 1, 2, 3, 4, 5, 6 });
        Assert.Equal(6, ring.Read(sink));
        Assert.Equal(new float[] { 1, 2, 3, 4, 5, 6 }, sink);

        ring.Write(new float[] { 7, 8, 9, 10, 11, 12, 13, 14 });
        Assert.Equal(6, ring.Read(sink));
        Assert.Equal(new float[] { 7, 8, 9, 10, 11, 12 }, sink);
        Assert.Equal(2, ring.Available);

        Assert.Equal(2, ring.Read(sink));
        Assert.Equal(new float[] { 13, 14, 0, 0, 0, 0 }, sink);
        Assert.Equal(4, ring.UnderrunSamples);
    }

    [Fact]
    public void Write_WithoutReader_StopsCountingOverflow_WhenSleeping()
    {
        // Источник без потребителя (нет маршрута до работающего выхода) не
        // должен копить миллионы «переполнений» — кольцо засыпает по таймауту.
        var ring = new SampleRing(8, sleepAfterMs: 40);
        var block = new float[8];

        ring.Write(block); // прогрев: помещается целиком
        Assert.Equal(0, ring.OverflowSamples);

        Thread.Sleep(60);
        Assert.True(ring.Sleeping);

        ring.Write(block); // кольцо полно, читателя нет — молча пропускаем
        ring.Write(block);
        Assert.Equal(0, ring.OverflowSamples);

        // Чтение будит кольцо: переполнения снова считаются.
        ring.Read(block);
        Assert.False(ring.Sleeping);

        ring.Write(block); // снова полно (8 из8)
        ring.Write(block);
        Assert.Equal(8, ring.OverflowSamples);
    }

    [Fact]
    public void Read_Underrun_CountsEvenForSleepingRing()
    {
        // Недостача при чтении — честный xrun даже для «спящего» кольца.
        var ring = new SampleRing(8, sleepAfterMs: 40);
        var block = new float[4];

        Thread.Sleep(60);
        Assert.True(ring.Sleeping);

        ring.Read(block);
        Assert.Equal(4, ring.UnderrunSamples);
        Assert.False(ring.Sleeping);
    }
}
