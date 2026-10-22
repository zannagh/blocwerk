// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Net;
using System.Net.Sockets;

namespace Blocwerk.Web.Endpoints.Feed;

/// <summary>
/// The partition key of a remote address for the feed's caps and rate limit. An IPv4 address (an IPv4-mapped IPv6 one
/// included) is itself; an IPv6 address is its /64, because one subscriber usually holds a whole /64 and can rotate through
/// it at will, so a per-address cap on the full address caps nothing.
/// </summary>
public static class FeedAddress
{
    public static string PartitionOf(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }
}
