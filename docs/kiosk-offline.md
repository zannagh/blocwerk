# A kiosk tablet without a connection

A paired kiosk tablet keeps a read-only copy of **its own wall** on the device. When the gym's connection drops, or the server is restarting, the tablet keeps showing that wall instead of an error page. This uses the same offline view as everyone else's phone: the page under `/v/`.

## What the tablet shows offline

* **Only the wall it is paired to.** Another wall's address shows nothing, and so does any data another browser session left on the device.
* **The kiosk view of that wall**, the same one the tablet shows online with nobody picked: published boulders only (no drafts, nothing archived), comments read-only, and members who have not agreed to appear on the kiosk shown as "A member". Setters keep their names.
* **Nothing about any member.** While a member is picked at the tablet, the offline view still shows the kiosk view. It shows no ticks, favourites, ratings or names of theirs, because none of them are ever stored on a tablet.
* **No grade preference.** A member's preferred grade scale is never stored on a tablet, so grades show in the wall's own scale.
* Offline, nothing can be edited: no ticks, comments, boulders or settings.

## When it shows "Not available offline"

* The tablet has not saved its wall yet. It saves the wall in the background after a page of the wall has been open online for a short while.
* The tablet may not keep a copy of its wall. The server then sends every answer as `no-store`, so the tablet keeps nothing. This is also what happens right after the tablet's kiosk key has been revoked or the tablet moved to another wall.
* The saved copy does not look like one wall's. The tablet then shows nothing rather than guess.

## What ends the copy

* **Unregistering the tablet** or **pairing it to another wall** deletes the copy on the next page load. The copy belongs to the pairing (the tablet's key and its wall), not to a person.
* **A member's release** ("Done" or the idle timeout) keeps it. Nothing in the copy belongs to that member.
* **"Clear offline data"**, offered on the offline page itself, deletes it at once. The tablet saves the wall again the next time it is online.

## Under the hood

* The offline page lives under `/v/`. A kiosk session may load it: `/v` is on the kiosk allow-list (`KioskRestrictions`). The page holds no data of its own. It reads only the device's cache.
* The device's cache is split into partitions. A tablet's is `k-` plus a keyed hash of its kiosk key and wall, the same for every identity on the tablet. A tablet that may not keep its wall gets the `none` partition, which stores nothing.
* The service worker hands a failed page load over to `/v/` for `k-` partitions, as it does for signed-in people (`u-`) and share links (`anon`).
* The offline page reads a `k-` partition only while it holds exactly one wall, and only when that wall's document is in the cache. It never reads marks under `k-`. The C# store applies the same one-wall rule again (`KioskStoreGate`).
