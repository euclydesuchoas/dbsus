using Xunit;

namespace DbSus.Client.Tests;

public class DatasusPeriodTests
{
    [Fact]
    public void OfMonth_KeepsYearAndMonth()
    {
        var period = DatasusPeriod.OfMonth(2024, 7);

        Assert.Equal(2024, period.Year);
        Assert.Equal(7, period.Month);
        Assert.Equal(DatasusPeriodicity.Monthly, period.Periodicity);
        Assert.Equal("2024-07", period.ToString());
    }

    [Fact]
    public void OfYear_HasNoMonth()
    {
        var period = DatasusPeriod.OfYear(1996);

        Assert.Equal(1996, period.Year);
        Assert.Null(period.Month);
        Assert.Equal(DatasusPeriodicity.Yearly, period.Periodicity);
        Assert.Equal("1996", period.ToString());
    }

    [Theory]
    [InlineData(0, 1, "year")]
    [InlineData(10000, 1, "year")]
    [InlineData(2024, 0, "month")]
    [InlineData(2024, 13, "month")]
    public void OfMonth_OutOfRange_Throws(int year, int month, string parameter)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => DatasusPeriod.OfMonth(year, month));

        Assert.Equal(parameter, exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10000)]
    public void OfYear_OutOfRange_Throws(int year)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DatasusPeriod.OfYear(year));
    }

    [Fact]
    public void Equality_SamePeriod_IsEqualWithTheSameHashCode()
    {
        var first = DatasusPeriod.OfMonth(2024, 7);
        var second = DatasusPeriod.OfMonth(2024, 7);

        Assert.True(first == second);
        Assert.False(first != second);
        Assert.True(first.Equals((object)second));
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Equal(DatasusPeriod.OfYear(2024), DatasusPeriod.OfYear(2024));
    }

    [Fact]
    public void Equality_AYearIsNeverEqualToAMonth()
    {
        var year = DatasusPeriod.OfYear(2024);

        for (var month = 1; month <= 12; month++)
        {
            Assert.NotEqual(year, DatasusPeriod.OfMonth(2024, month));
            Assert.True(year != DatasusPeriod.OfMonth(2024, month));
            Assert.NotEqual(0, year.CompareTo(DatasusPeriod.OfMonth(2024, month)));
        }

        Assert.False(year.Equals("2024"));
    }

    [Fact]
    public void Order_IsByYearThenMonth_AndAYearComesBeforeItsOwnMonths()
    {
        DatasusPeriod[] ordered =
        [
            DatasusPeriod.OfMonth(2023, 12),
            DatasusPeriod.OfYear(2024),
            DatasusPeriod.OfMonth(2024, 1),
            DatasusPeriod.OfMonth(2024, 2),
            DatasusPeriod.OfMonth(2024, 12),
            DatasusPeriod.OfYear(2025),
            DatasusPeriod.OfMonth(2025, 1),
        ];

        for (var i = 0; i < ordered.Length; i++)
        {
            for (var j = 0; j < ordered.Length; j++)
            {
                Assert.Equal(Math.Sign(i.CompareTo(j)), Math.Sign(ordered[i].CompareTo(ordered[j])));
                Assert.Equal(i < j, ordered[i] < ordered[j]);
                Assert.Equal(i <= j, ordered[i] <= ordered[j]);
                Assert.Equal(i > j, ordered[i] > ordered[j]);
                Assert.Equal(i >= j, ordered[i] >= ordered[j]);
                Assert.Equal(i == j, ordered[i] == ordered[j]);
            }
        }

        var shuffled = new[] { ordered[4], ordered[1], ordered[6], ordered[0], ordered[3], ordered[5], ordered[2] };
        Array.Sort(shuffled);
        Assert.Equal(ordered, shuffled);
    }

    [Fact]
    public void Default_IsNotAPeriod_AndComesBeforeEveryPeriod()
    {
        var unset = default(DatasusPeriod);

        Assert.Equal(0, unset.Year);
        Assert.Null(unset.Month);
        Assert.True(unset.IsDefault);
        Assert.True(unset < DatasusPeriod.OfYear(1));
        Assert.NotEqual(unset, DatasusPeriod.OfYear(1));
        Assert.False(DatasusPeriod.OfYear(1).IsDefault);
    }
}
