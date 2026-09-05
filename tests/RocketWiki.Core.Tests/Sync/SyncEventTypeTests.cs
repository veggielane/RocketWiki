using RocketWiki.Core.Enums;
using Xunit;

namespace RocketWiki.Core.Tests.Sync;

/// <summary>
/// The sync wire vocabulary is a contract between instances that upgrade at different
/// times (design.md §12), so its numbers and names are pinned here rather than left to
/// whatever the enum happens to say.
/// </summary>
public class SyncEventTypeTests
{
    /// <summary>
    /// Page entries were removed, and <c>SyncEventType.PageEntry</c> stayed behind as a
    /// reserved, retired member. Two things must hold for that to be safe, and each is
    /// a way the removal could quietly be undone.
    ///
    /// <para>The NUMBER stays 11 and is never given a new meaning: <c>SyncOutboxEvent</c>
    /// stores this tinyint, and a low side that produced entry rows before upgrading
    /// still holds them in its outbox — an 11 re-issued for something new would be read
    /// as that something by every instance that never had entries, which is how two
    /// instances silently corrupt each other. The NAME stays too, because it is what
    /// crosses on the wire and what <c>ParseEventType</c> accepts; the import side's
    /// skip depends on the name still parsing (see the bundle import tests).</para>
    /// </summary>
    [Fact]
    public void PageEntry_IsReservedAtEleven_AndKeepsItsWireName()
    {
        Assert.Equal((byte)11, (byte)SyncEventType.PageEntry);
        Assert.Equal("PageEntry", nameof(SyncEventType.PageEntry));
        Assert.True(Enum.IsDefined(SyncEventType.PageEntry));
        Assert.True(Enum.TryParse<SyncEventType>("PageEntry", out var parsed) && parsed == SyncEventType.PageEntry);
    }

    [Fact]
    public void NoTwoMembers_ShareAWireNumber()
    {
        // The reservation above is only worth anything if nothing else can claim the
        // value: a second member equal to 11 would compile, and Enum.GetName would then
        // return whichever the runtime found first.
        var values = Enum.GetValues<SyncEventType>().Select(v => (byte)v).ToList();
        Assert.Equal(values.Count, values.Distinct().Count());
    }
}
