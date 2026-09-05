using System.Diagnostics.CodeAnalysis;

namespace RocketWiki.Api.RealTime;

/// <summary>
/// A presence room key, parsed and classified. Presence used to exist only on pages, so
/// the room WAS the page id; site-wide presence needs any screen to be a room, and a bare
/// string would make "which authorization applies" a question every call site answers for
/// itself. This type makes the answer structural: parsing yields one of exactly three
/// shapes, and the hub has a branch for each.
///
/// <para><b>The prefix decides the gate, and there is no default gate.</b> An unrecognised
/// prefix does not fall through to "authenticated is good enough" — it fails to parse, and
/// a room that will not parse is refused. That ordering is the whole safety property here:
/// adding a fourth room type is a compile-time decision about how to authorize it, not a
/// runtime accident.</para>
///
/// <para>Keys are the SignalR group names too, so <see cref="Key"/> is canonical — a space
/// key is upper-cased through <c>SpaceKeys.Canonical</c> before it becomes a room, so
/// <c>space:eng</c> and <c>space:ENG</c> are one room rather than two half-populated
/// ones.</para>
/// </summary>
internal abstract record PresenceRoom
{
    private PresenceRoom(string key) => Key = key;

    /// <summary>The canonical key: the SignalR group name and the registry's dictionary key.</summary>
    public string Key { get; }

    /// <summary>
    /// A page's room, shared by every sub-route of that page (view, edit, history,
    /// details, properties, permissions) — they are one screen as far as "who else is
    /// looking at this" is concerned. Authorized by canView, exactly as JoinPage was.
    /// </summary>
    public sealed record Page : PresenceRoom
    {
        public Page(Guid pageId)
            : base($"page:{pageId}") => PageId = pageId;

        public Guid PageId { get; }
    }

    /// <summary>
    /// A space screen: the browser, trash, grants, analytics. Authorized by the caller
    /// holding any role in the space, and refused <b>identically</b> whether the space
    /// is invisible or absent (§6.7) — a space room must never become a way to discover
    /// that a space exists.
    ///
    /// <para><b>Authorization keys on the space; room identity keys on space + screen.</b>
    /// The two are deliberately different. Every screen of one space needs the same
    /// permission, so the gate reads <see cref="SpaceKey"/> alone — but someone reading
    /// the browser is not "here" on the trash screen, so those must be separate SignalR
    /// groups. Collapsing them into one key would put two screens in one room;
    /// collapsing the gate onto the full key would ask whether a space called
    /// "ENG:BROWSE" exists, which it never does — so every space room would fail closed,
    /// with §6.7 firing correctly on a question nobody meant to ask.</para>
    ///
    /// <para>The screen segment is an opaque client label and is NOT canonicalized: it
    /// names a layout, not a resource, and the server has no opinion about what layouts
    /// the SPA has.</para>
    /// </summary>
    public sealed record Space : PresenceRoom
    {
        public Space(string spaceKey, string? screen)
            : base(screen is null ? $"space:{spaceKey}" : $"space:{spaceKey}:{screen}")
        {
            SpaceKey = spaceKey;
            Screen = screen;
        }

        /// <summary>The canonical space key — the ONLY part the gate reads.</summary>
        public string SpaceKey { get; }

        /// <summary>The client's layout label, or null for a bare space room.</summary>
        public string? Screen { get; }
    }

    /// <summary>
    /// A global screen with no resource behind it — home, search, ask, settings, the
    /// admin section, a docs topic. <b>Authorized by being signed in, and nothing more.</b>
    ///
    /// <para>That is safe because the path names a CLIENT SCREEN, not a resource: it
    /// carries no page or space id, so there is nothing to authorize against and nothing
    /// to leak. A forged path just creates an isolated empty room that only its author
    /// is in. Validating paths against the real route table was considered and rejected —
    /// it is brittle for open-ended routes (docs topics, future screens) and buys no
    /// security, because the §6.7 gating that matters lives entirely on
    /// <see cref="Page"/> and <see cref="Space"/>.</para>
    ///
    /// <para>Bounded and control-char-free as hygiene only: the key becomes a SignalR
    /// group name and a dictionary key, so it should not be arbitrary binary or
    /// unbounded in length.</para>
    /// </summary>
    public sealed record Site : PresenceRoom
    {
        public Site(string route)
            : base($"site:{route}") => Route = route;

        public string Route { get; }
    }

    /// <summary>
    /// Parses a client-supplied room key. Returns false for anything unrecognised —
    /// unknown prefix, malformed page id, empty space key, or a site route that is not on
    /// the allowlist — and the caller refuses on false. The client controls this string
    /// entirely, so every branch here is reachable by an attacker and none of them may
    /// end in "assume it is fine".
    /// </summary>
    public static bool TryParse(string? raw, [NotNullWhen(true)] out PresenceRoom? room)
    {
        room = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        // Bounded before anything else: an unbounded key would be a memory-growth vector
        // even for a room that is ultimately refused, because parsing happens first.
        if (raw.Length > 200)
        {
            return false;
        }

        // No prefix, no room. A bare GUID is not a page room either: the page adapters
        // (JoinPage/LeavePage) build the prefixed key themselves, so nothing legitimate
        // sends one, and an unprefixed key would be a shape with no gate of its own.
        var separator = raw.IndexOf(':');
        if (separator <= 0 || separator == raw.Length - 1)
        {
            return false;
        }

        var prefix = raw[..separator];
        var value = raw[(separator + 1)..];

        switch (prefix)
        {
            case "page":
                if (Guid.TryParse(value, out var pageId))
                {
                    room = new Page(pageId);
                    return true;
                }

                return false;

            case "space":
                // The value is KEY[:screen]. Split on the FIRST colon only: the screen
                // segment is the client's own layout label and may itself contain
                // colons, while the key never does.
                var screenSeparator = value.IndexOf(':');
                var rawKey = screenSeparator < 0 ? value : value[..screenSeparator];
                var screen = screenSeparator < 0 ? null : value[(screenSeparator + 1)..];

                // Only the KEY is canonicalized — it is the thing the gate looks up, and
                // space keys are case-insensitive (§17). Canonicalizing the whole value
                // would ask whether a space named "ENG:BROWSE" exists; it never does, so
                // every space room would fail closed on a question nobody asked.
                var canonicalKey = Core.Services.SpaceKeys.Canonical(rawKey);
                if (canonicalKey.Length == 0 || screen is { Length: 0 })
                {
                    return false;
                }

                room = new Space(canonicalKey, screen);
                return true;

            case "site":
                // Hygiene only, not authorization: the path is a client screen label and
                // there is nothing behind it to authorize. Control characters are refused
                // because this string becomes a SignalR group name and a dictionary key.
                if (value.Any(char.IsControl))
                {
                    return false;
                }

                room = new Site(value);
                return true;

            default:
                return false;
        }
    }
}
