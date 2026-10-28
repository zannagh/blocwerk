# A kiosk tablet without a connection

A paired kiosk tablet keeps a read-only copy of **its own wall** on the device. When the gym's connection drops, or the server is restarting, the tablet keeps showing that wall instead of an error page. This uses the same offline view as everyone else's phone: the page under `/v/`.

## What the tablet shows offline

* **Only the wall it is paired to.** Another wall's address shows nothing, and so does any data another browser session left on the device.
* **The kiosk view of that wall**, the same one the tablet shows online with nobody picked: published boulders only (no drafts, nothing archived), comments read-only, and members who have not agreed to appear on the kiosk shown as "A member". Setters keep their names.
* **Nothing about any member.** While a member is picked at the tablet, the offline view still shows the kiosk view. It shows no ticks, favourites, ratings, preferences or names of theirs, because none of them are ever stored on a tablet.
* **Nothing can be edited**: no ticks, comments, boulders or settings.
* **It does not update while offline.** The tablet does not stream changes or poll for them. The copy is refreshed by the next page the tablet opens online.

## How long the copy is shown

* **For 7 days after the tablet last loaded its wall online**, as long as a signed-in person's offline data is kept after their session ends. After that, the tablet treats the copy as gone and shows the ordinary "You're offline" page until it is online again.
* So a tablet that is unregistered, moved to another wall or revoked **while it is offline** stops showing the old copy within those 7 days. Comments deleted and kiosk consent withdrawn in the meantime stop being shown at the latest then.

## When it shows "You're offline" or "Not available offline"

* **A tablet that may not keep a copy of its wall shows "You're offline".** This is the case right after its kiosk key has been revoked or the tablet moved to another wall. The server then gives the tablet the `none` partition, its next online page deletes the copy, and the tablet is never handed to the offline view.
* **"Not available offline"** is what the offline view itself shows on a tablet with nothing it may show:
  * the tablet has not saved its wall yet (it saves it in the background once a page of the wall has been open online for a short while);
  * the saved copy is incomplete, for example its wall document was never stored;
  * the saved copy does not look like one wall's. The tablet then shows nothing rather than guess.

## What ends the copy

* **Unregistering the tablet** or **pairing it to another wall** deletes the copy on the tablet's next page load online. The copy belongs to the pairing (the tablet's key and its wall), not to a person.
* **The 7-day window** above, while the tablet stays offline.
* **A member's release** ("Done" or the idle timeout) keeps it. Nothing in the copy belongs to that member.
* **"Clear offline data"**, offered on the offline page itself, deletes it at once. The tablet saves the wall again the next time it is online.

## Under the hood

* The offline page lives under `/v/`. A kiosk session may load it: `/v` is on the kiosk allow-list (`KioskRestrictions`). The page holds no data of its own. It reads only the device's cache.
* The device's cache is split into partitions. A tablet's is `k-` plus a keyed hash of its kiosk key and wall, the same for every identity on the tablet. A tablet that may not keep its wall gets the `none` partition, which stores nothing.
* The service worker hands a failed page load over to `/v/` for `k-` partitions, as it does for signed-in people (`u-`) and share links (`anon`).
  * For `k-` it does so only while the worker's stamp `/__bw/kiosk-fresh` names that partition and is at most 7 days old.
  * Every online page of the tablet writes that stamp after its fill (`client-precache.js`), with the time of the last successful manifest round.
* The offline page reads a `k-` partition only when all of these hold:
  * the partition holds exactly one wall;
  * that wall's document is in the cache;
  * the wall's last manifest success (`fetchedAt`) is within the same 7 days.

  It never reads marks under `k-`. The C# store applies the one-wall rule again (`KioskStoreGate`).
* A failed manifest round on the tablet never records a wall it had not stored, so a passing server blip on another wall's page cannot hide the bound wall offline.
* The tablet gets no grade-scale preference cookie, or any other per-member preference.
