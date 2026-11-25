# Anime Boxes (com.bisimplex.firebooru) Technical Specification

Reverse-engineered from the JADX output in `sources/com/bisimplex/firebooru` (`R.java` and `BuildConfig.java` excluded), plus `resources/AndroidManifest.xml` and `resources/assets`.

- **App:** "Anime boxes", package `com.bisimplex.firebooru`, versionName `2.1.0`, versionCode `119`, minSdk 26, targetSdk 36.
- **Core libraries:** OkHttp 3/4 (with `PersistentCookieJar` and `DnsOverHttps`), Gson, Jsoup, Glide 4 (with an OkHttp integration), ActiveAndroid (SQLite ORM), ObjectBox, WorkManager, Media3/ExoPlayer, libVLC, android-gif-drawable, PhotoView/ImageViewTouch, MaterialDrawer, Iconics, FastCSV, ACRA.
- **Paths:** All paths below are relative to `sources/com/bisimplex/firebooru/` unless stated otherwise.

> **Decompilation caveats.** Several files decompiled only as stubs: `custom/MySSLSocketFactory.java`, `custom/MyJXLDecoder.java`, `danbooru/DanbooruClient.java` and a few `widget/*` classes, each about 5 lines. A few methods are partially mangled, for example `ParserShimmieHTML.parseElement` and the inner download loop of `DownloadService`. Where behavior had to be inferred, this document says so.

---

## Table of contents

1. [API and parsing](#1-api--parsing)
2. [Storage and backup](#2-storage--backup)
3. [UI, theming and extras](#3-ui-theming--extras)
4. [Desktop upgrade opportunities](#4-desktop-upgrade-opportunities)
5. [Appendix: known bugs and quirks](#appendix-a--known-bugs--quirks-found-during-review)

---

## 1. API & Parsing

### 1.1 Architecture overview

```
SearchFragment / HomeFragment / PostListFragment
        │  SourceQuery (text, title, extraParams, initialPage, includeBlacklisted)
        ▼
SourceFactory ──creates──► Source<T>  (SourcePost, SourceMultiPost, SourceFavorites, SourceHistory,
        │                             SourcePool, SourceNote, SourceTag, SourceBooruTag)
        │  generateURLForCurrentPage()  ──► BooruProvider (per-server URL builder)
        ▼
HttpClient.getOkHttpClient()  ── enqueue ──► onResponse (OkHttp thread)
        │  body.string()
        ▼
Parser.fromProvider(ParserParams) ──► ParserXxx.parse()  → List<DanbooruPost | TagItem | NoteItem | Pool>
        │  (blacklist + favorite checks inside the parser)
        ▼
Main-thread Handler → Source.endParsing() → SourceListener.success()/failure()
```

| Class | Role |
|---|---|
| `danbooru/ServerItem` | Server configuration: URL, name, type, username, password, `passwordKey` (hashed), API key, rating filter, native-autocomplete flag. |
| `danbooru/ServerItemType` | Engine enum. The DB value is stored in the `server.isGelbooru` column. |
| `danbooru/BooruProvider` | Builds every URL for one `ServerItem`: posts, notes, pools, autocomplete, favorites, tag metadata. Also handles the blacklist, URL fixups, user agent and source-URL normalization. |
| `network/Source*` | Paging state machine: current page, page offset, `pageIndexes`, loading flag, last page, cancellation. |
| `network/Parser*` | Response-to-model conversion. JSON uses Gson `JsonParser` (tree model). XML and HTML use Jsoup. |
| `danbooru/DanbooruPost`, `DanbooruPostImage`, `TagItem`, `NoteItem`, `model/Pool` | Domain model. |
| `parser/*`, `danbooru/*ContentHandler`, `danbooru/DanbooruTasks`, `danbooru/SourceProvider` | **Legacy, dead code.** These are referenced only by each other or by the inactive widget. A rewrite can ignore them. |

### 1.2 Supported engines

`ServerItemType` (`danbooru/ServerItemType.java`). Bold names are the display names returned by `publicName()`.

| Int | Enum | Display name | Implemented? | Response format | Parser |
|---|---|---|---|---|---|
| 0 | `ServerItemTypeDanbooru` | **Danbooru** (Danbooru 1.x, Moebooru) | Yes | JSON | `ParserDanbooru1JSON` |
| 1 | `ServerItemTypeGelbooru` | **Gelbooru** (0.2.x DAPI) | Yes | JSON (whitelisted hosts) or XML | `ParserGelbooruJSON` / `ParserGelbooruXML` |
| 2 | `ServerItemTypeDanbooru2` | **Danbooru 2** | Yes | JSON | `ParserDanbooru2JSON` |
| 3 | `ServerItemTypeGelbooru111` | **Gelbooru 0.1.11** | Yes | HTML scraping | `ParserGelbooruHTML` |
| 4 | `ServerItemTypeShimmie` | **Shimmie 2** | Yes | HTML (current path); RSS parser also exists | `ParserShimmieHTML` / `ParserShimmie2` |
| 5 | `ServerItemTypeZeroChan` | ZeroChan | **No.** The enum exists but there is no URL builder. | n/a | n/a |
| 6 | `ServerItemTypeRSS` | RSS | **No.** The enum exists but there is no URL builder. | n/a | n/a |
| 7 | `ServerItemTypeIBSearch` | **IBSearch** | Yes | JSON | `ParserIBSearchJSON`. This class exists but `Parser.fromProvider` has no case for type 7, so it effectively returns `null`. |
| 8 | `ServerItemTypeDerpibooru` | **Philomena (Derpibooru)** | Yes | JSON | `ParserDerpibooruJSON` |
| 9 | `ServerItemTypeE621` | **e621** | Yes | JSON | `ParserE621JSON` |
| 10 | `ServerItemTypeHydrus` | **Hydrus** (Client API) | Yes | JSON, two-stage | `ParserHydrusJSON` |
| 11 | `ServerItemTypeBIO` | **BooruIO** | Yes | JSON | `ParserBooruIO` |
| 12 | `ServerItemTypeBooruOnRails` | **booru-on-rails** | Yes | JSON (Philomena-like v3) | `ParserBooruOnRails` |
| 13 | `ServerItemTypeKemono` | **Kemono** | Yes | JSON | `ParserKemono` |

**Default server.** The default server comes from manifest meta-data: `DEFAULT_SERVER_URL=https://donmai.moe`, `DEFAULT_SERVER_NAME="Default (https://donmai.moe)"`, `DEFAULT_SERVER_TYPE=2` (Danbooru 2).
- It is created or synchronized on every `DatabaseHelper.getDefaultServer()` call.
- The rating filter is enabled by default.
- The Top and Best presets are blanked for the default server.

**Version migration.** `UserConfiguration.firstLaunchInVersion()` forces any saved `https://e621.net` server to type e621.

### 1.3 Endpoint URL patterns

In the table below, `{url}` is the configured server URL with any trailing `/` removed. `{limit}` is the page size: 100 for posts, 16 for the widget. `{tags}` is the URL-encoded query.

| Engine | Post list (page appended last unless noted) | Ping / validation URL | Post page URL format (`postFormat`) |
|---|---|---|---|
| Danbooru 1 | `{url}/post/index.json?[login={u}&password_hash={h}&]limit={limit}&page={p}&tags={tags}` | `{url}/post/index.json[?login&password_hash]` | `{url}/post/show/{id}/` |
| Gelbooru | `{url}/index.php?[json=1&]page=dapi&s=post&q=index&limit={limit}{apiKeyRaw}&pid={p}&tags={tags}` | same with `limit=1` | `{url}/index.php?page=post&s=view&id={id}` |
| Gelbooru 0.1.11 | `{url}/index.php?page=post&s=list&pid={p*20}[&tags={tags}]`. **rule34.us special case:** `{url}/index.php?r=posts/index[&q={tags}][&page={p}]` | `{url}` | same as Gelbooru (for rule34.us: `{url}/index.php?r=posts/index&q=`) |
| Shimmie 2 | `{url}/post/list/{Uri.encode(tags)}/{p}`. With empty tags this becomes `/post/list//{p}`. | `{url}/post/list/1` | `{url}/post/view/{id}` |
| Danbooru 2 | `{url}/posts.json?[login={u}&api_key={k}&]limit={limit}&page={p}&tags={tags}` | `{url}/posts.json?[login&api_key&]limit=1` | `{url}/posts/{id}/` |
| IBSearch | `{url}/api/v1/images.json?[key={k}&]limit={limit}&page={p}&q={tags}`. An empty or `*` query becomes `sort:-upload`. | `…limit=1` | `{url}/images/{id}#image` |
| Philomena | `{url}/api/v1/json/search/images?key={k}&per_page=50&page={p}&q={tags}&{extraParams…}`. An empty query becomes `*`. | `{url}/api/v1/json/images/featured` | `{url}/{id}` |
| booru-on-rails | `{url}/api/v3/search/posts?key={k}&per_page=50&page={p}&q={tags}&{extraParams…}` | `{url}/api/v3/posts/featured` | `{url}/{id}` |
| e621 | `{url}/posts.json?v1=true&mode=extended&[login={u}&api_key={k}&]limit={limit}&page={p}&tags={tags}` | `{url}/posts.json?v2=true&[login&api_key&]limit=1` | `{url}/posts/{id}/` |
| Hydrus | **Stage 1:** `{url}/get_files/search_files?Hydrus-Client-API-Access-Key={k}&tags={jsonArray}&{extraParams…}`. **Stage 2:** `{url}/get_files/file_metadata?detailed_url_information=true&Hydrus-Client-API-Access-Key={k}&file_ids={jsonArray[≤100]}` | `{url}/verify_access_key?Hydrus-Client-API-Access-Key={k}` | n/a. The post URL is the first `url_type==0` known URL. |
| BooruIO | `{url}/api/legacy/query/entity?query={tags}[&cursor={p*50}]` | `{url}/api/legacy/query/entity` | `{url}/p/{key}` |
| Kemono | `{url}/api/v1/posts/?[q={tags}]&o={p*50}` (built with `Uri.Builder`) | `{url}/api/v1/posts` | `{url}/{service}/user/{user}/post/{id}` |

**Hydrus file URLs** (`ParserHydrusJSON.postWithFileId`):
- File: `{url}/get_files/file?Hydrus-Client-API-Access-Key={k}&file_id={id}`
- Thumbnail: `{url}/get_files/thumbnail?Hydrus-Client-API-Access-Key={k}&file_id={id}`

**Kemono file URLs:**
- File: `{url}{attachment.path}`
- Thumbnail: `{url}/thumbnail/data{path}`

**Browser ("open on site") URLs** (`generateSearchUrl`):
- Gelbooru and Gelbooru 0.1.11: `index.php?page=post&s=list&tags=`
- Danbooru 1: `/post?searchDefault=Search&tags=`
- Danbooru 2: `/posts?utf8=✓&commit=Go&tags=`
- Shimmie: `/post/list/{tags}`
- IBSearch: `/images/?q=`
- Philomena and booru-on-rails: `/search?q=`
- e621: `/posts?tags=`

### 1.4 Query preprocessing (`BooruProvider.generateRequestUrl`)

1. **Automatic rating filter.** If `ServerItem.ratingFilterEnabled` is on and the query does not already contain the word "rating", a safe-rating term is appended:

   | Engine | Appended term |
   |---|---|
   | Danbooru 1, Gelbooru 0.1.11, e621 | `rating:safe` |
   | Shimmie | `rating=s` |
   | IBSearch | `rating:s` |
   | Gelbooru (JSON hosts) | `rating:general` |
   | Gelbooru (XML hosts) | `rating:safe` |
   | Danbooru 2 on `donmai.*` hosts (non-default server only) | `rating:g` |
   | Other Danbooru 2 hosts (non-default server only) | `rating:safe` |

2. **Macro.** `#fullhd` becomes `width:>=1920`.
3. **Extra tags.** `SourceQuery.extraParams["extra_tags"]` is appended to the text and removed from the parameter map. Sort pickers in the search dialog feed it: `order:*` for e621, Gelbooru and Danbooru 2, plus `is:`/`has:` filters for Danbooru 2.
4. **Wildcard normalization:**
   - Danbooru 2, Gelbooru, Gelbooru 0.1.11 and BIO: `*` becomes empty.
   - Philomena and booru-on-rails: empty becomes `*`.
   - Hydrus: empty or `*` becomes `system:everything`.
   - Kemono: `*` becomes empty.
5. **Force original image.** If the query contains `width:` or `height:`, `enforceOriginalImage` is set and the viewer shows the full file instead of the sample.
6. **Hydrus tag format.** Tags are comma-separated (`BlacklistRule.SITE_SEPARATOR`) and serialized as a JSON array. The tag separator for display and joining is `", "` for Hydrus and Philomena and `" "` everywhere else (`getTagSeparator()`).
7. **Extra query parameters.** Philomena, booru-on-rails and Hydrus append every remaining `extraParams` entry as `&k=v`. Hydrus URL-encodes the values.

**"Top" and "Best" presets:**

| Engine | Top | Best |
|---|---|---|
| Danbooru 1 | `order:score` | `date:>{yesterday} order:votes` |
| Gelbooru | `sort:score` | `sort:date score:>=5` |
| Danbooru 2, e621 | `order:score` | (none) |

**Search dialog sort keys** (`dataadapter/search/SearchDialogAdapter.java`):

| Engine | Keys |
|---|---|
| Philomena | `sf` (sort field) and `sd` (`asc`/`desc`). Sort fields: `first_seen_at`, `updated_at`, `score`, `wilson_score`, `_score`, `width`, `height`, `comment_count`, `tag_count`, `pixels`, `size`, `faves`, `upvotes`, `downvotes`, `aspect_ratio`, `random`. |
| booru-on-rails | `filter_id` |
| Hydrus | `file_sort_type` (int) and `file_sort_asc` (`true`/`false`) |

### 1.5 Authentication and headers

| Engine | Mechanism |
|---|---|
| Danbooru 1 | `login` plus `password_hash` query parameters. `password_hash = sha1(salt.replace("your-password", pw))`, where the salt depends on the host: konachan uses `So-I-Heard-You-Like-Mupkids-?--your-password--`; yukkuri.shii uses `masaorz-r5quenm--your-password--`; sakugabooru uses `er@!$rjiajd0$!dkaopc350!Y%)--your-password--`; every other host uses `choujin-steiner--your-password--`. |
| Danbooru 2, e621 | `login` plus `api_key` query parameters. If the server is in the `uc_server_with_cookies` list, Danbooru 2 file, sample and preview URLs also get `login`/`api_key` appended (`ParserDanbooru2JSON.addCredentialsIfNeeded`). |
| Gelbooru | The API key field holds a **raw query suffix** pasted by the user, for example `&api_key=…&user_id=…`. It is concatenated unmodified into the post, note, tag and autocomplete URLs. `FailureType.InvalidGelbooruCredentials (-502)` exists for this case. |
| Philomena, booru-on-rails, IBSearch | `key` query parameter. |
| Hydrus | `Hydrus-Client-API-Access-Key` as a query parameter. `add_url` also sends it as a header. |
| Shimmie | Header `Cookie: agreed=true` (bypasses the age gate). |
| BooruIO | Header `Accept-Encoding: identity`. |

**User-Agent:**
- e621 gets `Anime boxes/2.1.0 (by Bisimplex)`.
- Every other server gets the system WebView UA string. It is captured once with `new WebView().getSettings().getUserAgentString()` and cached in prefs as `default_ua`.

**Image requests** (Glide, `network/Utils.urlFor*`) send:
- `Referer: {scheme}://{postHost}/`
- `User-Agent: <as above>`
- `Connection: keep-alive`

**Cookies:**
- `custom/CustomPersistentCookieJar` persists cookies to SharedPreferences.
- **Quirk:** `loadForRequest` also sends a cookie when `requestHost.contains(cookie.domain)`. This is looser than RFC 6265.
- The in-app `BrowserFragment` (a WebView) lets users log in or clear Cloudflare challenges. `SourceFactory.cookiesUpdatedFor(host)` then resets the affected pinned sources.

**HTTP client** (`network/HttpClient`):
- One shared OkHttp client.
- 120 s connect, read and write timeouts.
- `LoggingInterceptor`.
- Optional proxy (`model/ProxyConfiguration`): type 0 = direct, 1 = HTTP, 2 = SOCKS, with Basic `Proxy-Authorization`.
- Optional DNS-over-HTTPS: Google (`https://dns.google/dns-query`, bootstrap 8.8.4.4 and 8.8.8.8) or Cloudflare (`https://1.1.1.1/dns-query`, IPv4 only).
- Glide has its own OkHttp client (`custom/MyGlideModule`) with 5 min connect, 10 min read/write/call timeouts, the same cookie jar, proxy and DNS settings, and a progress-reporting network interceptor.

**Cleartext traffic** is allowed (`usesCleartextTraffic="true"`). The setting `AllowInvalidCertificates` exists, but its SSL factory decompiled as an empty stub.

### 1.6 Pagination model (`network/Source.java`)

| Engine | First page | Increment | Page size | End detection |
|---|---|---|---|---|
| Danbooru 1/2, e621, IBSearch, Philomena, booru-on-rails, Shimmie | 1 | +1 | 100 (Philomena: 50) | Empty result list |
| Gelbooru | 0 (`pid`) | +1 | 100 | Empty |
| Gelbooru 0.1.11 | 0 | `pid = page*20` | ~20 (site-defined) | Empty |
| BooruIO | 0 | `cursor = page*50` | 50 | Empty |
| Kemono | 0 | `o = page*50` | 50 | Empty |
| Hydrus | Stage 1 returns **all** file IDs. Stage 2 pages through those IDs in chunks of 100, offset by the current item count. | | 100 | `file_ids.size() == itemCount` |

**Paging state machine:**
- `prepareLoadAnotherPage()`: if this is a new search, `currentPage = initialPageNumber + pageOffset`; otherwise `currentPage++`.
- `pageOffset` supports "jump to page" (`JumpToPageDialog`, `SourceQuery.initialPage`).
- `pageIndexes` records the cumulative item count after each page. `pageNumberOfItemIndex(i)` turns this into the grid's floating "Page N" label.
- `isLastPage` is set when a page parses to zero items.
- If the blacklist removed every post on a page, `SourcePostBasic.forceLoadNextPage()` offers to keep paging (UI: "N hidden").
- HTTP errors map to `danbooru/FailureType`: Connection (0), ContentCannotBeParsed (-500), InvalidCert (-501), InvalidGelbooruCredentials (-502), 403, 404, 420 InvalidRecord, 421 UserThrottled, 422 Locked, 423 AlreadyExist, 424 InvalidParameters, 429, 500, 503.
- For HTML-parsed servers (Shimmie), a non-2xx response is treated as an empty page, which ends paging.
- A Gelbooru JSON response that is a bare string primitive is shown to the user as a **server message**.

**Multi-server search** (`SourceMultiPost`):
- One `SourcePost` is created per selected server, with history recording disabled.
- `loadAnotherPage()` fires all sources in parallel.
- Results are buffered in `pageMatrix` until **every** source finishes, then interleaved round-robin (item *i* from each source in turn).
- `getProvider(post)` resolves which server a given post came from.

**Throttling.** None. A `minimumMilisecondDelay` field and a scheduled executor exist but are unused.

### 1.7 Post parsing: field mapping and quirks

Common model: `DanbooruPost`.

| Field group | Fields |
|---|---|
| Identity | `postId`, `postUrl` (from `postFormat`) |
| Images | `file`, `sample`, `jpeg`, `preview`, each a `DanbooruPostImage{url, width, height, ext, contentType}` |
| Tags | `tags`, `tag_general`, `tag_artist`, `tag_character`, `tag_copyright`, `tag_meta`, `separateTags: List<TagItem>` |
| Metadata | `md5`, `rating`, `score`, `source`, `author`, `parent_id`, `has_children`, `has_notes`, `has_comments`, `created_at` |
| Flags | `isFavorite`, `blacklisted`, `disableStorage` (never persisted to history or favorites; set for Philomena, booru-on-rails and Hydrus) |

**Visible version** (`DanbooruPost.getVisibleVersion()`): the app shows `file` when `sample` is null, when `enforceOriginalImage` is set, or when the global `downloadOriginalImage` setting is on. Otherwise it shows `sample`.

**Extension and content type** (`DanbooruPostImage.setUrl`):
- The extension comes from the `ext=` query parameter if present, otherwise from the last path segment. A `?` suffix is stripped.
- Content types: Static, Gif, WebM, MKV, MOV, AVI. MP4 is detected by extension.
- `isAnimated = gif || video`.
- `ParserPosts.supportedExtensions = " jpg jpeg png gif mp4 webm webp mkv mov avi "`.

**Per-engine parsing:**

| Parser | Key mapping and quirks |
|---|---|
| `ParserDanbooru1JSON` | Reads `tags`, `rating`, `sample_url/width/height`, `preview_url…`, `jpeg_url…`, `file_url`, `width`, `height`, `md5`, `parent_id`, `author`, `score`, `source`, `created_at`, `has_children`, `has_comments`. If the sample is empty, or the file is animated but the sample is not, the sample is set to the file. On `donmai.` hosts, `/ssd/` in the preview URL is rewritten to `http://sonohara.donmai.us/`. `has_notes` is always true. |
| `ParserDanbooru2JSON` | Posts with no `md5` are skipped. If `file_url` is missing, the parser synthesizes `{url}/data/{md5}.{ext}`. When `has_large` is set, the sample is `large_file_url`, falling back to `{url}/data/sample/sample-{md5}.{jpg}` (PNG becomes JPG). If `preview_file_url` is missing, it becomes `{url}/ssd/data/preview/{md5}.jpg`. In `media_asset.variants[]`, a type starting with `360` overrides the preview and type `sample` overrides the sample URL and size. A `zip` file (ugoira) with a video sample uses the sample as the file. `tag_string_model` is merged into general tags. `tag_string_meta` becomes `tag_meta`. `has_notes = last_noted_at != null`. `is_favorited` is used for favorite sync. `author = uploader_id`. |
| `ParserGelbooruJSON` | Root is `{post:[…]}` or a bare array; a string primitive becomes the server message. Reads `id`, `score`, `md5` (or `hash`), `rating`, `source`, `owner`, `tags`, `has_notes`, `has_children`, `parent_id`, `preview_url…`, `file_url`, `width`, `height`, `sample_url…`. `created_at` uses the format `E MMM d HH:mm:ss Z yyyy`. Tags or sources containing HTML entities are unescaped with `Html.fromHtml`. **rule34.xxx:** `?t={now}` is appended to file and sample URLs (`shouldAvoidPolish`) to defeat a CDN "polish" cache. |
| `ParserGelbooruXML` | Selects `<post>` elements and reads the same fields as attributes. When `shouldFixURLs` is set (non-JSON hosts), it rewrites preview extensions to `.jpg` and rewrites PNG/JPEG/GIF sample extensions to `.jpg` when the sample size differs from the file size. It collapses the sample into the file when they are identical. `fixDanbooruPostURLs` and `fixImageURL` (rebuilding `/images/{md5[0:2]}/{md5[2:4]}/…`) exist but are unused. |
| `ParserGelbooruHTML` (0.1.11) | Uses `span.thumb`, falling back to `.thumbail-container a` (sic). The ID comes from an `<a id="p123">` with the prefix stripped. The thumbnail comes from `img[data-src]` or `img[src]`. The file URL is guessed by rewriting `thumbs`→`img`, `/thumbnails/`→`//images/`, removing `thumbnail_` and changing `//aimg.`→`//img.`. `img@title` carries the tags plus `score:N` and `rating:x`, which are extracted with a regex. A tag containing `video` (but not `video game` or `video_game`) changes the extension to `.webm`, and for lazy-load layouts the host is rewritten to `video.{domain}`. A tag containing `animated` changes the extension to `.gif`. |
| `ParserShimmieHTML` | Selects `.thumb` elements and reads `data-tags`, `data-post-id`, and `data-ext`/`data-fileext`/`data-mime` from the dataset. In `<a>` elements, `.shm-thumb-link` or `/post/view` links give the post ID and preview; other links give a direct file link, with `?ext=` appended if needed. If there is no file link, the file URL is guessed from the thumbnail (`thumbs`→`images`, `thumb`→`{id}`). A single-post page falls back to `#main_image`. MD5 is taken from the second-to-last path segment of the thumbnail URL. The server URL's subpath is stripped from the base URL. |
| `ParserShimmie2` (RSS) | Reads `<item>`: `title` is `"{id} - {tags}"`, plus `link`/`guid`, `description` (size from a `\d+x\d+` regex), `media:thumbnail@url` and `media:content@url`. Spaces become `%20` and subpath prefixes are fixed. `md5 = md5(fileUrl)`. |
| `ParserDerpibooruJSON` | Root is `{images:[…], interactions:[…]}`. Any `interactions[].interaction_type=="faved"` sets `isFavorite` for that `image_id`. Uses `representations.full/large/thumb` as file, sample and preview; protocol-relative and root-relative URLs are fixed. A WebM thumbnail gets a `.gif` extension. `tags[]` is zipped with `tag_ids[]`, and tags containing `artist:` are typed as artist. `md5 = orig_sha512_hash ?? sha512_hash ?? md5(postUrl)`. Also reads `source_url`, `uploader`, `comment_count`. |
| `ParserBooruOnRails` | Root is `{posts:[…]}`. Mapping matches Philomena. Posts with `hidden_from_users` are skipped. |
| `ParserE621JSON` | Root is `{posts:[…]}` or an array. Skips `flags.deleted` posts and `.swf` files. `files.original` or `file` gives the file. Each image object reads `url ?? webp ?? jpg` plus `width`/`height`, and is null when `has == false`. `sample` and `preview` come from the same object. `relationships.parent_id` and `has_children`; `has.active_children` and `has.notes`. `sources[]` is joined with `" | "`. `score.total`. `stats.is_favorited`, `comment_count`. Tag groups: general, species, character, copyright, artist, invalid, lore, meta. Species, lore and invalid are prepended into general. `author = uploader_name ?? uploader_id`. |
| `ParserHydrusJSON` | **Stage 1** (`{file_ids:[…]}`): stores the IDs, then `SourcePostBasic` immediately requests stage 2. **Stage 2** (`{metadata:[…], services:{…}}`): the `rating_service_key` is the first service with `type == 7` (like/dislike), and `ratings[key]` maps to `isFavorite`. Reads `hash`, `ext` (unsupported extensions are dropped), `width`, `height`. In `detailed_known_urls[]`, the first `url_type==0` normalised URL becomes `postUrl` and the second becomes `source`. Otherwise `known_urls[0]` becomes `source`. `time_modified_details.local` becomes `created_at`. Tags come from `service_names_to_statuses_to_tags["all known tags" | "my tags" | "my tag repository"]["0"]`, or the newer `tags[*]` entry with `type==10`, `display_tags["0"]`. Namespaces: `creator:` = artist, `character:`/`person:` = character, `series:`/`title:` = copyright. Results are re-sorted to match the stage 1 ID order. |
| `ParserBooruIO` | Root is `{data:[…]}`. `key` is the post ID. `attributes.width/height`. `tags` is an object whose keys may carry a type suffix (`#artist`, `#character`, `#origin`). `transforms` is a map of size to path, each turned into `{url}/api/legacy/data/{path}` and sorted by the numeric size: smallest = preview, largest = file, middle = sample. Posts with fewer than 2 transforms are dropped. |
| `ParserKemono` | Root is `{posts:[…]}`. **Each post fans out into one `DanbooruPost` per unique file or attachment path** with a supported extension. `postId = id`, `created_at = published`, `source = title`. MD5 is the file name without its extension. A video thumbnail's extension is blanked. |
| `ParserIBSearchJSON` | File: `{proto}://{server}.{host}/{path}`. Thumbnail: `{proto}://{server}.{host}/t{path}`. |

**Common post-parse steps:**
- **Blacklist.** `shouldAddToResults()` calls `BooruProvider.isBlacklisted()` and increments `removedCount`. The `includeBlacklisted` query flag or `disableVisibilityChecks` bypasses it.
- **Favorite reconciliation.** `ParserPosts.parseFinished()`, when "hide favorites" is on, and `SourcePost.checkParsedPostsAreFavoritesAsync()`, always, look up `(postid, md5)` in the local `favorite` table. Remote favorites from Danbooru 2, e621 and Philomena are auto-inserted locally. The visible grid is then refreshed with `reloadVisible()`.
- **Hide favorites.** When `uc_hide_favorites` is on, posts already in favorites are removed from results.
- **Tag-category cache.** For Danbooru 2 and e621 (`canFeedTagMetadata`), `BooruTagHelper.feedWithSourceAsync()` stores tag-to-category pairs in the ObjectBox `BooruTag` cache. Engines without per-category tag strings use this cache to color tags.

### 1.8 Tag autocomplete

`SourceTag` with page size 8. `prepareLoadAnotherPage()` first runs a **local** parse with no response body, then the network request:

1. **Local results** (`ParserTag.parse()`): matches from search history (type 7, shown first, case-insensitive substring), then the ObjectBox `BooruTag` cache (`tagsForAutocomplete`). Results are deduplicated by lowercase name.
2. **Remote results.** If the `AUTOCOMPLETE_FROM_SERVER` setting is on and the server supports it:

   | Engine | URL | Parser | Mapping |
   |---|---|---|---|
   | Gelbooru (gelbooru.com only) | `{url}/index.php?page=autocomplete2&term={t}&type=tag_query&limit=10{apiKeyRaw}` | `ParserTagAutoGelbooruJSON` | Only `type=="tag"`. `value`, `post_count`, `category` (tag/artist/character/copyright). |
   | Danbooru 2 | `{url}/tags.json?search[order]=count&search[hide_empty]=true&limit=10&only=id,name,category,post_count&search[name_matches]=*{t}*[&api_key&login]` | `ParserTagAutoDanbooruJSON` | Skips `is_deprecated`. |
   | e621 | `{url}/tags.json?search[name_matches]={t}*&search[order]=count&limit=10&q={t}**[&api_key&login]` | `ParserTagAutoEJSON` | Category 7 (meta) maps to 5. |
   | Philomena | `{url}/api/v1/json/search/tags?q={t}*[&key]` | `ParserTagAutoPhiloJSON` | `images` is the count. Categories character/oc/species = character; content-fanmade/official = copyright; names containing `artist:` = artist. |
   | Hydrus | `{url}/add_tags/search_tags?tag_display_type=display&search={t}&Hydrus-Client-API-Access-Key={k}` | `ParserTagAutoHydrusJSON` | `tags[].value`, `count`, namespace typing. |

3. **Fallback.** Any other case uses a **third-party** service: `https://bisimplex.azurewebsites.net/api/tags?limit=8&name={t}`, parsed by `ParserTag` (`Id/Name/Type/Count` or the lowercase variants).

**Tag-category metadata lookup** (`SourceBooruTag`, gelbooru.com only): `{url}/index.php?[json=1&]page=dapi&s=tag&q=index&limit=100&names={space-separated}{apiKeyRaw}`. The JSON response is `{tag:[{id,name,count,type}]}`; the XML response is `<tag name type>`.

**`TagItem.type` codes:**

| Code | Meaning |
|---|---|
| 0 | General |
| 1 | Artist |
| 3 | Copyright |
| 4 | Character |
| 5 | Meta |
| 7 | Search history |

### 1.9 Pools

| Engine | Search URL (`SourcePool`, page size 20) | Parser | How the pool's posts are queried |
|---|---|---|---|
| Danbooru 1 | `{url}/pool/index.json?query={q}&page={p}` | `ParserPoolDanbooru2JSON` | `pool:{id}` |
| Danbooru 2 | `{url}/pools.json?search[name_matches]={q}&search[sort]=name&commit=Search&page={p}[&login&api_key]` | `ParserPoolDanbooru2JSON` (`id`, `name`, `description`, `post_count`) | `pool:{id}` |
| e621 | `{url}/pools.json?search[name_matches]={q}&search[order]=name&search[is_deleted]=false&page={p}[&login&api_key]` | `ParserPoolDanbooru2JSON` | `pool:{id}` |
| Philomena | `{url}/api/v1/json/search/galleries?[key&]q=title:{q}*&page={p}`. An empty query becomes `title:*`. | `ParserPoolDRPB` (`id`, `title`, `description`, `spoiler_warning` shown as a "[Spoiler]" prefix, `user` shown as "By …") | `gallery_id:{id}` |

- Pool page URL: `{url}/pool/show/{id}`, or `{url}/galleries/{id}` for Philomena.
- `*` is stripped from pool queries.
- `PoolServersFragment` lists only servers that support pools (`DatabaseHelper.loadServersWithPools`).
- Saved pools are stored in the `poolFavorite` table.

### 1.10 Notes (translation overlays)

`SourceNote`. Notes load per post on demand, or automatically when `uc_AUTO_LOAD_NOTES` is on.

| Engine | URL | Format |
|---|---|---|
| Danbooru 1 | `{url}/note/index.json?post_id={id}` | JSON |
| Danbooru 2, e621 | `{url}/notes.json?group_by=note&search[post_id]={id}&page=1[&login&api_key]` | JSON |
| Gelbooru | `{url}/index.php?page=dapi&s=note&q=index&post_id={id}{apiKeyRaw}` | XML `<note x y width height body>` |

- JSON fields: `x`, `y`, `width`, `height`, `body`. Inactive notes (`is_active:false`) are skipped.
- The body is HTML, flattened to plain text with `Flatten.flattenHTML`.
- Each frame is scaled by `proportion = visibleImage.width / file.width`, because notes are authored against the original file resolution.
- **Quirk:** the frame is stored in an Android `Rect(x, y, width, height)`, so its right/bottom slots hold width and height, not edges.

### 1.11 Remote favorites (sync)

Remote favorite sync is enabled by the `FavSyncDanbooru2` setting (default on). It runs in `SourceFavorites.syncFavorite()`.

| Engine | Add | Remove |
|---|---|---|
| Danbooru 1 | `POST {url}/post/vote.json`, body `{"id":…,"score":"3","login":…,"password_hash":…}` | Same request with `"score":"2"` |
| Danbooru 2 | `POST {url}/favorites?login&api_key&post_id={id}`, body `{}` | `DELETE {url}/favorites/{id}?login&api_key` |
| e621 | `POST {url}/favorites.json?login&api_key`, body `{"post_id":id}` | `DELETE {url}/favorites/{id}.json?login&api_key` |
| Philomena | `POST {url}/images/{id}/fave?key={k}`, body `{"_method":"POST"}` | `DELETE` to the same URL |
| Hydrus | `POST {url}/edit_ratings/set_rating`, body `{"hash","rating_service_key","rating":true,"Hydrus-Client-API-Access-Key"}` | Same request with `"rating":null` |

**Send to Hydrus.** `network/HydrusClient` posts `POST {hydrus}/add_urls/add_url` with body `{"url": postUrl, "destination_page_name":"Anime boxes"}` for a selection of posts or for the whole filtered favorites list. It runs on a single-thread executor.

### 1.12 Source-link normalization

`BooruProvider.fixSourcePost` splits the source on `|`. Pixiv image or CDN URLs (`pixiv.net/`, `pximg.net/`) that lack `illust_id=` or `/artworks/` are rewritten to `https://www.pixiv.net/en/artworks/{id}`, where the ID is the file name before the first `_`.

### 1.13 Other network calls

| Call | Details |
|---|---|
| Update check | `GET https://www.animebox.es/def.json`. If `{version}` is greater than 119 and not yet announced, the app broadcasts `NEWVERSIONAPP`. |
| Deep link | `anibox://` with query parameters `server`, `query`, `user` (`MainActivity.onNewIntent`). |

---

## 2. Storage & Backup

There are three persistence layers. A rewrite should merge them into one.

| Layer | Technology | Location | Contents |
|---|---|---|---|
| Relational | **ActiveAndroid** (SQLite ORM) | `idanbooru.db`, `AA_DB_VERSION=1` (manifest) | Servers, favorites, post history, search history, blacklist, pool favorites |
| Object store | **ObjectBox** (`model/MyObjectBox`, `model/ObjectBox`) | App files dir | Tag-category cache, download queue, metadata-update queue |
| Key-value | `SharedPreferences("UserConfiguration")`, `SecurePreferences("uc_sec")` (AES), `SharedPrefsCookiePersistor` | prefs XML | Settings, home pins (JSON), app-lock pattern, cookies |

### 2.1 SQLite schema (ActiveAndroid)

ActiveAndroid adds an implicit `Id INTEGER PRIMARY KEY AUTOINCREMENT` to every table. **No secondary indexes are declared.**

**`server`** (`model/Server`)

| Column | Type | Notes |
|---|---|---|
| `url` | TEXT | Base URL |
| `serverName` | TEXT | |
| `isGelbooru` | INT | **Actually stores the `ServerItemType` int value** (legacy name) |
| `userName`, `password` | TEXT | Password stored in **plaintext** |
| `passwordKey` | TEXT | Danbooru 1 SHA1 password hash |
| `apikey` | TEXT | API key, or the Gelbooru raw query suffix |
| `isDefault`, `isSelected`, `ratingFilterEnabled`, `useNativeAutocomplete` | INT (0/1) | Shimmie forces `ratingFilterEnabled=false` on load |

**`favorite`** (`model/Favorite`) and **`Post`** (`model/Post`, viewed-post history) share these columns:

| Column | Type |
|---|---|
| `postid`, `posturl`, `md5`, `rating`, `source`, `parent_id`, `tags` | TEXT |
| `tag_string_general`, `tag_string_artist`, `tag_string_character`, `tag_string_copyright` | TEXT |
| `file_url`, `width`, `height` | TEXT, INT, INT |
| `sample_url`, `sample_width`, `sample_height` | TEXT, INT, INT |
| `jpeg_url`, `jpeg_width`, `jpeg_height` | TEXT, INT, INT |
| `preview_url`, `preview_width`, `preview_height` | TEXT, INT, INT |
| `score`, `has_notes`, `has_comments`, `has_children` | INT |
| `dateAdded` | DATE (stored as a long) |
| `isHistory` | BOOL (`Post` only) |

**`history`** (`model/TagHistory`, search history): `search` TEXT, `lowercaseSearch` TEXT, `searchDate` DATE, `isFavoritedHistoryItem` INT (starred).

**`bannedTag`** (`model/BannedTag`, blacklist): `tagText` TEXT. The `bannedTagid` field is legacy.

**`poolFavorite`** (`model/PoolFavorite`): `poolId`, `poolName`, `poolDescription`, `poolUrl`, `poolIsFavorite`, `poolAddedDate`, `server` (FK to `server.Id`).

**Behavioral rules** (`danbooru/DatabaseHelper`):

- **Favorites:**
  - Identity is `(postid, md5)`, or `postid` alone when md5 is empty.
  - Posts with `disableStorage` (Philomena, booru-on-rails, Hydrus) are never written.
  - `addFavoriteItemNoCheck` is used by restore.
- **Favorites filter** (`loadRawFavoritesByFilter`, driven by `SourceFavorites.FILTER_*` extra params):

  | Filter | SQL |
  |---|---|
  | Each search word | `tags LIKE '%w%'` (AND) |
  | `FILTER_EXCLUDE_TAGS` | `tags NOT LIKE '%w%'` per word |
  | `FILTER_SERVER_URL` | `posturl LIKE '%u%'` |
  | `FILTER_EXT` | `file_url LIKE '%ext%'` |
  | `FILTER_RATING` | `rating LIKE 'x%'` (first character) |
  | `FILTER_SOURCE` | `source LIKE '%s%'` |

  Sort (`FavoriteSortType`): Date (`Id desc`), DateAsc, Score, ScoreAsc, Random (`RANDOM()`). Pages are 100 rows (`limit 100 offset page*100`).
  **Quirk:** substring `LIKE` gives false positives, for example `cat` matches `catgirl`.
- **Post history:** duplicates on `(postid, md5)` are ignored. The table is capped at about 100 rows; older rows are deleted with `ORDER BY Id DESC LIMIT 100 OFFSET 100`.
- **Search history:**
  - An existing search has its `searchDate` bumped instead of being inserted again.
  - Unstarred entries are capped at 1000; the oldest is deleted on each insert past the cap.
  - Ordering is starred first, then by date descending.
  - Recording happens in `SourcePost.loadAnotherPage()` on **every page load**, unless `disableHistory` is set (multi-search and pinned sources).
- **Servers:**
  - `getSelectedServer` reads `isSelected=1`.
  - The default server cannot be deleted.
  - `loadServersByType` has a pseudo-type 2 that means "Danbooru 1, Danbooru 2 or e621".
  - `getServerByUrl` matches on host.

### 2.2 Blacklist rule grammar (`model/BlacklistRule`)

Each `bannedTag.tagText` row is one rule made of space-separated tokens. **All** tokens must match (logical AND):

| Token | Meaning |
|---|---|
| `tag` | The post's tag list (padded with spaces) contains ` tag ` |
| `-tag` | The post does **not** contain ` tag ` |
| `rating:x` | The first letter of the post's rating equals the first letter of `x` (case-insensitive) |
| `user:name` | The post author equals `name` |
| `site:url1,url2` | The rule applies only when the current server URL contains one of these. It is added by `DatabaseHelper.buildTagText` when a rule is scoped to specific servers. |

- Rules are loaded per provider when it is created (`BooruProvider.updateBannedTags()`).
- When a rule matches, the post gets `blacklisted=true` and `blacklistRule` is set. The grid shows a "blacklisted" label if `uc_include_blacklisted` is on; otherwise the post is dropped.

### 2.3 ObjectBox entities

| Entity | Fields | Purpose |
|---|---|---|
| `BooruTag` | `id` (long), `type` (int), `hits` (long), `name` (String, indexed) | Tag-name to category cache used for coloring and autocomplete (`services/BooruTagHelper`, `services/BooruTagSorter`) |
| `DownloadEntry` | `id`, `preview_url`, `sample_url`, `file_url`, `post_id`, `post_url`, `tags`, `rating`, `md5`, `source`, `tag_general/copyright/character/artist`, `query`, `date_added`, `download_date`, `file_name`, `extension`, `status`, `avoid_duplicate`, `exclude_animated`, `error_message`, `error_code`, `target_folder` | Persistent download queue. `status`: 0 = waiting, 1 = downloading, 2 = downloaded, 3 = failed, 4 = duplicated. |
| `UpdateEntry` | `id`, `fav_id`, `status`, `message`, `update_date`, `added_date` | Queue for refreshing favorite metadata (`services/UpdateMetadataService`) |

### 2.4 Settings (`danbooru/UserConfiguration`, SharedPreferences `"UserConfiguration"`)

| Key | Type | Default | Meaning |
|---|---|---|---|
| `MaterialThemeType` | int | 0 (Dark) | `ThemeType`: Dark 0, Light 1, Gray 2, Purple 3 |
| `uc_ThumbDisplayMode` | int | 0 (Classic) | `ThumbDisplayMode`: Classic 0, NoCrop 1, Staggered −2, Big 2, BigNoCrop 3 |
| `downloadOriginalImage` | bool | false | Show and download the original file instead of the sample |
| `PreloadType` | int | 0 (Wi-Fi) | 0 = Wi-Fi only, 1 = always (note: `PRELOAD_TYPE_DISABLED` is also 1) |
| `uc_preload_count` | int | 1 | Number of viewer posts to preload ahead |
| `uc_quality_render` | int | Low | `QualityRenderType`: None, ForcedLow, Low, Normal. Normal uses ARGB_8888; otherwise RGB_565. **Overwritten to Low on every launch.** |
| `uc_limit_image_size` / `uc_DeviceMaxImageSize` | bool / int | true / 2048 or 4096 (4096 when density > 2) | Glide downsample target for full images |
| `isReduceImageSize` | bool | true | |
| `uc_streamVideo` | bool | true | Stream video instead of downloading it first |
| `uc_VideoDecoder_1` | int | 0 (Base) | `VideoDecoderType`: Base (WebView) 0, VLC 1, EXO 2 |
| `uc_video_muted` | bool | false | |
| `uc_AUTO_LOAD_NOTES` | bool | true | |
| `uc_auto_load_pins` | bool | true | Home pins load automatically |
| `uc_hide_favorites` | bool | false | Remove already-favorited posts from search results |
| `uc_include_blacklisted` | bool | false | Show blacklisted posts with a label instead of hiding them |
| `uc_fix_gelbooru_video_links` | bool | true | |
| `uc_longTapToDownload` | bool | true | |
| `uc_doubleTap_cmd`, `uc_swipeUp_cmd`, `uc_swipeDown_cmd` | int | 0 (None) | `ViewerCommandType`: None, ShowInfo, Save, ToggleFavorite, ViewNormal, ViewOriginal, Share, ToggleNotes, Back, ZoomIn, ZoomOut |
| `uc_VOLUME_NAVIGATION` | bool | false | Volume keys page through the viewer |
| `ic_ShowViewerNavigationButtons` | bool | false | |
| `uc_screen_on_viewer` | bool | true | Keep the screen on in the viewer |
| `uc_secure_screen` | bool | false | `FLAG_SECURE` (no screenshots or recents preview) |
| `uc_status_bar_visible`, `uc_user_hide_nav_bars`, `UC_user_top_bar` | bool | false / true / true | Immersive viewer options |
| `uc_file_name_type` | int | MD5 (0) | `FileNameType`: MD5 0 (configurable), Legacy 1 |
| `uc_fileNameConfiguration` | JSON list | | Ordered `FileNamePartType` list: MD5 0, Domain 1, Tags 2, ID 3 |
| `sdPath` | string | | Download tree URI (SAF) |
| `uc_DNS_type` | int | 0 | `DNSType`: System, Google, CloudFlare |
| `uc_Proxy` | JSON | null | `ProxyConfiguration{type, host, port, user, pass}` |
| `AUTOCOMPLETE_FROM_SERVER` | bool | false | Use per-server autocomplete instead of the Bisimplex service |
| `FavSyncDanbooru2` | bool | true | Push favorites to the remote server |
| `CombineMultiSearch` / `uc_multi_search_ids` | bool / list | false | Multi-server search configuration |
| `uc_specs_list` | JSON | | Home pins (`List<SourceSpecs>`) |
| `uc_server_with_cookies` | list | | Server URLs authenticated through the in-app browser |
| `ASK_CRASH_REPORTS` | bool | true | ACRA consent |
| `default_ua`, `uc_version_check`, `lastVersionChecked`, `mustShowFullScreenTutorial`, `yumekui`, `GifFullScreen`, `ThumbFullScreen`, `AllowInvalidCertificates` | misc | | Internal flags |

**Secure prefs** (`custom/SecurePreferences`):
- Store name `uc_sec`.
- Values are encrypted with AES/CBC/PKCS5; keys are encrypted with AES/ECB; the key is derived with SHA-256 from a **hard-coded passphrase** `e45WNnwSAy4gZ688Edq2`.
- The only entry is `LOCK_KEY`, a JSON array of the cell indexes in the unlock pattern.

**Home pins** (`model/SourceSpecs`): `{key (UUID), url (server URL), type, query: SourceQuery, childs: List<SourceSpecs>}`. Type is NONE 0, FAVORITE 1, HISTORY 2, SEARCH 3 or GROUP 4. Groups nest children.

### 2.5 Backup and restore (`backup/`)

**Entry points.** `BackupFragment` (runs `BackupTask`) and `RestoreFragment` (runs `LoadLiveDBTask`, then `RestoreTask`). Both are coordinated by `BackupManager`. I/O goes through the SAF tree URI (`DocumentFile`) on a single-thread executor.

**`BackupConfiguration`** flags: `favorites`, `servers`, `bannedTags`, `history`, `homeSources`, `toCSV`.

#### 2.5.1 JSON format (`.abbj`)

- File name: `{localized date-time}.abbj`, MIME `text/json`.
- Gson serialization of `LiveDB`:
  - Date format `yyyy-MM-dd'T'HH:mm:ssZ`.
  - `excludeFieldsWithModifiers(1176)` (static, final, transient, abstract are skipped).
  - Custom adapters for `BackupServerItemType` and `DanbooruPostContentType`.

```jsonc
{
  "backupTime": "2024-01-01T12:00:00+0000",
  "backupVersion": "1.0",
  "creatorName": "Anime boxes (Android)",
  "creatorVersion": "2.1.0",
  "servers": [ { "serverId", "url", "serverName", "type": <BackupServerItemType int>,
                 "userName", "password", "passwordKey", "apiKey", "apikey",
                 "isDefault", "isSelected", "ratingFilterEnabled", "useNativeAutocomplete", "realURL" } ],
  "favorites": [ <DanbooruPost serialized: postId, postUrl, md5, tags, tag_*, file/sample/jpeg/preview{url,width,height,ext,contentType}, rating, score, source, parent_id, has_*, created_at, dateAdded, ...> ],
  "rawFavorites": [ <Favorite rows> ],          // accepted on restore
  "searchHistory": [ { "itemId", "searchText", "searchDate", "starred" } ],
  "bannedTags": [ { "tagText", "bannedTagid" } ],
  "homePins": [ <SourceSpecs> ]
}
```

**Server type codes in backups** (`BackupServerItemType`) **differ from the database codes**, probably to stay compatible with the iOS app:

| Engine | Backup code | DB code |
|---|---|---|
| Danbooru | 0 | 0 |
| Gelbooru | 1 | 1 |
| Shimmie | **2** | 4 |
| Danbooru 2 | **3** | 2 |
| Gelbooru 0.1.11 | **4** | 3 |
| ZeroChan | 5 | 5 |
| RSS | 6 | 6 |
| IBSearch | 7 | 7 |
| Philomena | 8 | 8 |
| e621 | 9 | 9 |
| Hydrus | 10 | 10 |
| BIO | 11 | 11 |
| booru-on-rails | 12 | 12 |
| Kemono | 13 | 13 |

#### 2.5.2 CSV format

- Library: FastCSV. MIME `text/csv`.
- Section headers are written as comment lines (`#Meta`, `#Servers`, `#History`, `#Favorites`, `#Tag Blacklist`, `#Home Pins`).
- The first column is the row type (`BackupCSVRowType`): Server 0, History 1, Favorite 2, Blacklist 3, HomePin 4, Meta 5.
- Rows are padded with trailing empty columns for forward compatibility.
- Dates use `yyyy-MM-dd'T'HH:mm:ssZ`.

| Row type | Columns after the type |
|---|---|
| 5 Meta | backupVersion, creatorName, creatorVersion, backupTime, then 5 blank columns |
| 0 Server | serverId, url, serverName, useNativeAutocomplete, ratingFilterEnabled, isSelected, isDefault, type (BackupServerItemType), userName, password, realURL, apikey, apiKey, then 5 blank columns |
| 1 History | itemId, searchText, searchDate, starred, then 5 blank columns |
| 2 Favorite | postid, posturl, sample_width, sample_height, sample_url, preview_width, preview_height, preview_url, width, height, file_url, jpeg_width, jpeg_height, jpeg_url, tags, tag_string_general, tag_string_artist, tag_string_character, tag_string_copyright, md5, source, parent_id, score, rating, has_notes, has_comments, has_children, dateAdded, then 10 blank columns (favorites are streamed 100 at a time) |
| 3 Blacklist | tagText, bannedTagid, then 5 blank columns |
| 4 HomePin | parentKey (empty for top level), key, url, type, query (Gson JSON of `SourceQuery`), then 5 blank columns. Children are written recursively. |

A separate **"export posts to CSV"** feature (`BackupCVSTask`, `BackupCVSOptions{fileNamePrefix, fileURL, postURL, posts|query}`) writes file and/or post URLs for a list of posts or a favorites filter.

#### 2.5.3 Restore semantics (`RestoreTask`, merge only, never wipes)

1. **Banned tags:** each rule is added (duplicates are rejected by `addBannedTag`).
2. **Servers:** non-default servers are matched by URL (case-insensitive). With the "overwrite" option, the existing row is updated; otherwise only new URLs are inserted. Gelbooru servers that carry a username and password are flagged as needing re-validation.
3. **Favorites** (`favorites` and/or `rawFavorites`): with `checkDuplicates` on, a favorite is skipped if its `posturl` already exists. Inserts use `addFavoriteItemNoCheck` or `Favorite.save()`.
4. **Search history:** merged with `addHistoryItem(text, date, starred)`.
5. **Home pins:** appended unless an equal `SourceSpecs` already exists.

Result codes (`RestoreStatusType`): Success, InvalidFile, PermissionDenied, Error, None, CannotOpenFile, NotFound, DoesntExist, CannotReadLine, CannotBeParsed, InvalidParameters.

> **Security note:** backups contain server passwords and API keys in plaintext.

---

## 3. UI, Theming & Extras

### 3.1 Screen hierarchy

```
SkeletonActivity (AppCompat; schedules CacheCleanUpWorker on stop; cancels it on start)
 └─ MenuBaseActivity (MaterialDrawer: left nav drawer + right "secondary" panel [InfoPanel | History])
     └─ MainActivity (fragment host; intent/deep-link handling; LockFragment gate; download/metadata service wiring)
         └─ widget/ViewerActivity (MainActivity subclass for widget taps — widget inactive, see 3.9)
```

**Left navigation drawer** (`MenuBaseActivity`, in order). Top and Hot are shown only when the server defines those presets.

| Item | Fragment / action |
|---|---|
| Home | `HomeFragment` (home pins) |
| Search | `SearchFragment` (opens a `SearchDialog` bottom sheet) |
| Browse all | `PostListFragment` with an empty query |
| Top / Hot now | `PostListFragment` with `topString` / `bestString` |
| Favorites | `FavoriteListFragment` |
| History | `HistoryFragment` (viewed posts) |
| Pools | `PoolServersFragment`, then `PoolsFragment` |
| Downloads | `DownloadsFragment` |
| Servers | `ServersFragment`, then `ServerFormFragment` / `DynamicServerFormFragment` |
| Tag blacklist | `TagsBlackListFragment`, then `TagBlackListFormFragment` |
| Settings | `DynamicSettingsFragment` (form driven by `SettingsDataAdapter`). Sub-screens: `ProxyFormFragment`, `DynamicFileNameFragment`, `BackupFragment`, `RestoreFragment`, `SortPinFragment` |
| Community / Buy me a coffee / Feedback / Help | External links (`R.string.community_url`, `help_url`, `twitter_url`) |

**Right panel:**
- **InfoPanel:** tags of the visible post grouped by category. Tapping a tag opens `TagMenuDialog`: search, add to query (`SourceQuery.plus`), exclude (`minus`), blacklist, copy. Also shows ID, author, parent/child and source links (`SecondaryMenuActionType`).
- **History:** search history (`HistoryTagMenuDialog`: star, delete, search).

**Other screens:**

| Component | Description |
|---|---|
| `MultiSearchFragment` / `MixedSearchFragment` | Multi-server combined results (`SourceMultiPost`) |
| `TagHistoryFragment` | Search-history list |
| `InfoFragment` | Post details |
| `BrowserFragment` | In-app WebView used for login and cookies |

**Dialogs:**

| Dialog | Purpose |
|---|---|
| `SearchDialog` / `DynamicSearchDialog` | Query, server selector, multi-server selector, engine-specific sort options, page selector |
| `FilterFavorieDialog` / `DynamicFavoriteSearchDialog` | Favorites filters |
| `DynamicPoolSearchDialog`, `DynamicBlacklistSearchDialog`, `DynamicServerSearchDialog` | Search within those lists |
| `JumpToPageDialog` | Sets the starting page |
| `SlideshowDialog` | Slideshow interval |
| `SourceSpecsDialog` / `SourceSpecsEditDialog` / `GroupSpecsDialog` / `RenameGroupSpecsDialog` / `PinDialog` | Pin management |
| `ServersDialog` | Server picker |
| `ValidateClientDialog` | Pings `pingServiceUrl` to check credentials |
| `GelbooruUserRequestDialog` | Gelbooru API key help |
| `LinkMenuDialog` | Source links |
| `FlagDialog`, `ReviveDialog`, `DelayedProgressDialog`, `CustomDialog` | Miscellaneous |

**Messages** use `activity/MessageType` (toasts, snackbars and "Minimal" banners).

### 3.2 Home screen (pins)

- `HomeFragment` renders `UserConfiguration.getSourceSpecs()` through `HomeAdapter`.
- Holder types: `TitleHomeItemHolder`, `SublistHomeItemHolder` (a horizontal strip of thumbnails backed by a live `SourcePostBasic` from `SourceFactory.createSourceForSpecs`), `SublistCollapsedHomeItemHolder`, `GroupHomeItemHolder`, `ButtonHomeItemHolder`.
- Pinned sources live in the `SourceType.Permanent` map and survive navigation. History and Favorites pins reset when shown again.
- Item actions (`data/ItemActionType`): Search, Browse, Delete, Reload, LoadPage, MoveUp/Down/First/Last, AddToGroup, ShowGroup, Rename, SortName(Desc), Edit, Expand.
- `SortPinFragment` handles drag-sort.

### 3.3 Thumbnail grid ("masonry")

Implemented in `fragment/PostListFragment` and `dataadapter/GridPostDataAdapter`.

**Layout manager:**
- **Staggered** (true masonry): `StaggeredGridLayoutManager(R.integer.grid_num_cols, VERTICAL)` with `GAP_HANDLING_MOVE_ITEMS_BETWEEN_SPANS`. Uses layout `free_form_item_grid_2`.
- **Fixed grid:** `GridLayoutManager(columns)`, where `columns = floor(screenWidthDp / 120)`, or `/ 176` for the Big modes.

**Cell layouts by mode:**

| Mode | Layout |
|---|---|
| Classic | `complex_item_grid_image` (square crop) |
| NoCrop | `free_form_item_grid_2` |
| Big | `complex_item_grid_image_big` |
| BigNoCrop | `free_form_item_grid_big_2` |

- An optional header cell (type 1, `source_grid`) shows the source label.
- `setHasFixedSize(true)`.
- Batches are added with `notifyItemRangeInserted`; a full reset uses `notifyDataSetChanged`.

**Infinite scroll.** When a cell is bound (`attachedToWindow(position)`), the next page loads if `position >= itemCount - 4`, the source is not loading, and it is not on the last page.

**Thumbnail loading:**
- Glide request built with `Utils.getThumbsOptions()`: `DiskCacheStrategy.DATA`, `PREFER_RGB_565`, hardware bitmaps disallowed, no animation, `R.drawable.empty` placeholder.
- Headers: Referer and UA.
- **Gelbooru thumbnail host failover** (`thumbDrawableRequestListener`): on a load failure, `img2.` is retried as `img3.`, `img3.` as `img1.`, and `thumbs.gelbooru.com` as `img3.gelbooru.com/thumbnails/`. The corrected URL is saved back to the favorite row on success.

**Cell overlays:**
- Heart (favorite).
- Animated or video icon.
- "Blacklisted" label.
- Extension color badge (`DanbooruPostImage.getColorForExtension`).
- Underlined text label in some modes.

**Page indicator.** A floating "Page N" label tracks the last visible position through `Source.pageNumberOfItemIndex`.

**Item interactions:**
- Tap opens the viewer (`DetailFragment`), sharing the same `Source` key.
- Long-press enters multi-select or a context menu, with download, favorite, send-to-Hydrus and export-CSV actions.

**Empty states:**
- "No results".
- "N posts hidden by blacklist", which triggers `forceLoadNextPage()`.
- The server message (Gelbooru).

### 3.4 Media viewer

Implemented in `fragment/DetailFragment`, `fragment/DetailPagerAdapter` and `view/*`.

**Pager.** `ViewPager2` over the same `SourcePostBasic` list. `setVisiblePostIndex` keeps the grid scroll position in sync when returning to the grid (via `postDelayed(200ms)` + `scrollToPosition`).

**Page view types.** There are two: `ImageDetailPagerHolder` (type 0) and `VideoPagerHolder` (type 1, when `visibleVersion.isVideo()`).

**Images:**
- `view/BooruPhotoView` (PhotoView) handles pinch and double-tap zoom.
- While loading, the preview thumbnail shows first (thumbnail options); then the full image loads with `Utils.getImageOptions()`: `DATA` disk cache, `dontTransform`, `DownsampleStrategy.CENTER_INSIDE`, `override(deviceMaxImageSize)` (2048 or 4096 px), and RGB_565 unless the quality setting is Normal.
- Download progress comes from `MyGlideModule.expect(url, listener)`, which feeds a progress-reporting `ResponseBody` wrapper.
- GIFs decode as Glide `GifDrawable`. A legacy `PageGifImageView` (android-gif-drawable, built from a downloaded file) also exists.
- Failed loads show a retry UI with error details (`FailureType`, Glide root causes).

**Video.** A **single shared** `IVideoView` instance is reparented into the current page (`attachVideoView`). The decoder comes from `uc_VideoDecoder_1`:

| Setting | Implementation |
|---|---|
| Base (0) | `ChromeVideoView`: a WebView that loads `assets/video.html`, an HTML5 `<video id="player">`. It talks to native code through a `@JavascriptInterface` named `Android` (`onError`, `onPause`, play events and timing). If no WebView package is installed, `NoChromeVideoView` (Android `VideoView`) is used instead. |
| VLC (1) | `VLCVideoView` (libVLC). VLC's Lua scripts (`assets/lua/meta`, `modules`, `playlist`) and an HRTF file (`assets/hrtfs/dodeca_and_7channel_3DSL_HRTF.sofa`) are bundled. |
| EXO (2) | `ExoVideoView` (Media3 ExoPlayer) |

- With `uc_streamVideo` on, playback streams the URL with the post URL as Referer.
- With it off, the file is downloaded first (Glide `asFile`) and played locally.
- Mute state is persisted (`uc_video_muted`). Timer labels show position and duration. `VideoErrorType` drives the error UI.

**Preloading.** On page change, if `getAllowPreload(isOnWifi)` allows it, the next `uc_preload_count` posts are prefetched (thumbnail plus full image, or file for animated posts) on a background executor. When the visible post is the last loaded item, the next page loads automatically.

**Slideshow.** `SlideshowDialog` sets the interval in seconds. GIF duration is respected (`PageView.getDuration()`). Video pages are skipped.

**Gestures and controls:**
- Double-tap command, swipe-up and swipe-down commands (each configurable to any `ViewerCommandType`).
- Optional on-screen up/down buttons.
- Volume-key navigation.
- Long-tap to download.
- Toggling between sample and original (`ViewNormal` / `ViewOriginal` set `enforceOriginalImage` and reload).
- Share (file via `GenericFileProvider`, or URL).
- Notes toggle. Notes are drawn as scaled overlays (§1.10).

**Window flags:**
- `FLAG_KEEP_SCREEN_ON` (`uc_screen_on_viewer`).
- `FLAG_SECURE` (`uc_secure_screen`).
- Immersive mode (hidden status and navigation bars).
- A one-time full-screen tutorial.

**History.** A viewed post is written to `Post` history once its image finishes loading (`DownloadTarget` with `_addToHistory`).

**Variants:** `FavoriteDetailFragment` and `HistoryDetailFragment` browse local lists; `WidgetDetailFragment` is for the widget.

### 3.5 Downloads (`services/DownloadService`)

- Singleton with **one** single-thread executor, so downloads are strictly sequential.
- `enqueueWork(posts, DownloadOptions{downloadOriginal, excludeAnimated, avoidDuplicates, target_folder}, query)`:
  1. Builds `DownloadEntry` rows in ObjectBox (status 0). File name comes from `DanbooruPost.generateDownloadFileName()`.
  2. Clears finished entries (statuses 2 and 4).
  3. Starts the worker.
- **On app launch** (`checkIfMustLaunch`), entries stuck in "downloading" (1) are reset to "waiting" (0) and the queue resumes.
- **Worker loop, per entry:**
  1. Fetch the file through `Glide.asFile()`, which reuses the Glide disk cache, with Referer and UA from `Utils.urlForEntry`.
  2. Write it either to the SAF tree (`DocumentFile.createFile` in `target_folder`, or `sdPath`) or, without a tree, to **MediaStore** (`Images` or `Video` `EXTERNAL_CONTENT_URI`, with `_display_name`, `mime_type` `image/{ext}` or video, `title`).
  3. When `avoid_duplicate` is set, skip existing names (status 4).
  4. On failure, store `error_message` and `error_code` (status 3) and delete any partial file (`deleteFailedIfNeeded`).
- Notification channel `com.bisimplex.firebooru.ANDROID` ("Anime boxes") shows progress.
- `DownloadsFragment` / `DownloadsAdapter` list the entries with their status, and support retry and cancel-all.

**File naming** (`DanbooruPost.generateCurrentFileName`):
- **Configurable mode:** joins the selected parts with spaces: MD5, Domain (post URL host), Tags (artist + copyright + character + general + meta, trimmed to ≤70 characters at a word boundary), ID. Then the extension is appended, and the result is sanitized to `[0-9a-zA-Z+.()_\-\s]`. **Bug:** no `.` is inserted before the extension.
- **Legacy mode:** `"{host} {postId} {tags…}"` up to 90 characters, then `.ext`.

### 3.6 Background services

| Service | Description |
|---|---|
| `UpdateMetadataService` | Refreshes favorites: for each queued `UpdateEntry`, queries the favorite's original server with `id:{postid}` (`id={postid}` on Shimmie) and replaces stale URLs and md5. Started from `FavoriteListFragment`. |
| `CacheCleanUpWorker` | WorkManager one-time job, enqueued as a unique job with KEEP in `SkeletonActivity.onStop` and cancelled in `onStart`. Deletes files in `image_cache` older than a threshold, at most 1200 files per run. The Glide disk cache is 1000 MB (`ExternalPreferredCacheDiskCacheFactory`). |
| `BooruTagHelper` | Single-thread tag-category cache population and lookup (`processTagsAsync` per post in the viewer). |
| `HydrusClient` | Queue for sending URLs to Hydrus (§1.11). |

### 3.7 Theming (`skin/SkinManager`)

- `ThemeType` is Dark (default), Light, Gray or Purple. Each maps to color resources: accent, primary, primaryDark, action-bar icon, activity background, text, window background, semi-transparent scrim, and success text (the accent color).
- `reloadTheme()` reads `MaterialThemeType`. Activities apply the theme on creation, so a restart is required.
- Icons come from Iconics (FontAwesome and Octicons).

### 3.8 App lock (`lock/`, `fragment/LockFragment`)

- `GestureLock` is a **3×3 pattern grid** (`depth = 3`) of `GestureLockView` cells. Cell modes: normal 256, selected 512, error 1024.
- Modes: `MODE_NORMAL` (verify) and `MODE_EDIT` (record). After 5 failed attempts, `onUnmatchedExceedBoundary` fires (`unmatchedBoundary = 5`); currently it only shows a toast, with no lockout.
- The pattern is stored as a JSON list of cell indexes in `SecurePreferences` under `LOCK_KEY` (§2.4).
- **Gate timing:** `showLock` is computed **once**, in `UserConfiguration.setContext()` at process start, and cleared by `passedLock()`. **The app does not re-lock when resumed from the background**; `AppLifecycleListener` is empty. The lock is effectively cold-start only.

### 3.9 Widget (`widget/`)

- `StackWidgetService` and `StackRemoteViewsFactory` load 16 posts (`new BooruProvider(server, 16)`) for a per-widget server and query (`WidgetConfigurationManager`). Tapping opens `ViewerActivity`.
- **The manifest declares no `AppWidgetProvider` receiver and no `RemoteViewsService`, so the widget is inactive in this build.**

### 3.10 Manifest and assets summary

**Permissions:** INTERNET, ACCESS_NETWORK_STATE, POST_NOTIFICATIONS, READ/WRITE_EXTERNAL_STORAGE (≤ API 32), WAKE_LOCK, RECEIVE_BOOT_COMPLETED, FOREGROUND_SERVICE.

**Components:**
- `MainActivity` (launcher plus the `anibox:` VIEW intent filter).
- `ViewerActivity`.
- ActiveAndroid ContentProvider.
- `GenericFileProvider` (sharing).
- WorkManager services and receivers.
- ACRA crash reporting (separate `:acra` process).
- Glide OkHttp module.

**Meta flags:** `ALLOW_FLAG=false`, `PURCHASE_ENABLED=false`, `MARKET_FORMAT_URL=amzn://apps/android?p=%s` (an Amazon Appstore build), plus a leftover Fabric API key.

**Assets:**

| Asset | Purpose |
|---|---|
| `video.html` | HTML5 player page for `ChromeVideoView` |
| `lua/**` | VLC Lua extensions (art fetchers, playlist parsers, dkjson, simplexml) |
| `hrtfs/*.sofa` | VLC spatial-audio HRTF |
| `PublicSuffixDatabase.list` | OkHttp public-suffix list (cookie domain checks) |
| `dexopt/baseline.prof[m]` | ART baseline profile |

---

## 4. Desktop Upgrade Opportunities

The items below are specific to this codebase. Each lists the mobile limitation, where it lives, and the recommended Windows desktop design. The stack assumed is .NET 8 with WinUI 3 or Avalonia, but the points apply to any native stack.

### 4.1 Networking and parsing

| # | Mobile bottleneck (where) | Desktop improvement |
|---|---|---|
| N1 | **Whole-body string buffering plus Gson tree parsing.** `Source.onResponse` calls `body.string()`, then `JsonParser.parseString`, then walks the tree. Every 100-post page is fully materialized twice. | Stream-deserialize with `System.Text.Json` source-generated `JsonSerializerContext` (or simdjson) straight from the response stream into typed DTOs per engine. For HTML engines use AngleSharp with compiled selectors. |
| N2 | **Strictly serial paging.** One in-flight call per `Source`; `cancelCurrentConnection()` runs before every page. | Prefetch page N+1 when the user passes about 50% of page N. Keep an LRU of pages. Allow 2 to 4 concurrent page fetches per host through a per-host `SocketsHttpHandler` connection pool with HTTP/2 where available. |
| N3 | **Multi-server search waits for the slowest server** (`SourceMultiPost.success` mixes only when `getIsLoading()` is false for all sources). | Stream results as each server answers, using `IAsyncEnumerable` merged with `Channel<T>`. Insert into the grid incrementally. Add per-server timeouts and partial-failure badges. |
| N4 | **No rate limiting or backoff** (`minimumMilisecondDelay` is unused). 429 and 421 errors surface as failures. | Per-host token buckets, for example Danbooru about 10 req/s and e621 at 1 to 2 req/s as their policies require. Honor `Retry-After`. Retry 5xx responses with exponential backoff and jitter. This matters more on desktop, where faster scrolling issues more requests. |
| N5 | **Hydrus two-stage fetch pulls every file ID first** (`search_files` with no limit, then metadata in pages of 100). | Keep the ID list, but fetch metadata for visible and near-visible windows only (virtualized) and in parallel. Cache metadata by `file_id`. |
| N6 | **Duplicated, divergent URL builders.** One 1000-line `BooruProvider` with `if (type == …)` chains; Gelbooru credentials stored as a raw query suffix; magic per-host rules (rule34.us, rule34.xxx, donmai, gelbooru.com). | Use an `IBooruEngine` interface per engine (`BuildPostsUrl`, `ParsePosts`, `Autocomplete`, `Notes`, `Pools`, `Favorite`) plus declarative **host quirk profiles** in JSON (rating term, wildcard handling, CDN cache-buster, thumbnail-host failover list). Store Gelbooru `api_key` and `user_id` as separate fields. |
| N7 | **Third-party autocomplete by default** (`bisimplex.azurewebsites.net`), which leaks queries and stops working if that service goes away. | Use native server autocomplete by default. Add a **local tag index** (see S3) for instant results while typing; debounce the network call by 150 ms. |
| N8 | **Images come from Glide's OkHttp client with a separate cookie, proxy and DNS configuration**, which can drift from the API client. | Use one `HttpClient` factory for API, image and download traffic, configured once with the proxy, DoH and cookie settings. |
| N9 | **Loose cookie domain matching** (`host.contains(domain)`). | Use the RFC 6265 `CookieContainer`. Import cookies from the embedded WebView2 (`CoreWebView2.CookieManager`) after interactive login or a Cloudflare challenge, which replaces `BrowserFragment`. |
| N10 | **Every page load writes a history row** (`SourcePost.addHistoryItem` runs per page). | Record a search once per new query. |

### 4.2 Storage

| # | Mobile bottleneck | Desktop improvement |
|---|---|---|
| S1 | **Three stores** (ActiveAndroid SQLite, ObjectBox, SharedPreferences JSON blobs). Home pins and multi-search IDs are JSON inside prefs. | Use a single SQLite database through Microsoft.Data.Sqlite or EF Core in WAL mode, with migrations. Settings go in a typed `settings.json`, or a `settings` table. |
| S2 | **Favorite queries use `LIKE '%tag%'` with no indexes**: full scans and false positives (`cat` matches `catgirl`). | Normalize tags: `tags(id, name, category)` and `post_tags(post_id, tag_id)` with an index on `tag_id`. Add an **FTS5** virtual table for free text. Support exact tag AND/NOT, `rating:`, `ext:` and `site:` as indexed predicates. Add indexes on `(server_id, post_id)`, `md5` and `date_added`. |
| S3 | **Per-post favorite lookups**: `getIsFavByPost` runs once per result (100 queries per page) on a single executor, then `reloadVisible()` re-renders. | Run one batched `SELECT … WHERE (post_id, md5) IN (…)` per page, or keep an in-memory `HashSet<(server, postId)>` warmed at startup, which is cheap on desktop. |
| S4 | **Small history caps** (100 viewed posts, 1000 searches) sized for mobile. | Make the caps configurable, unlimited by default, with pruning by age. |
| S5 | **Secrets stored in plaintext** (`server.password`, `apikey`, the proxy password, backups). `SecurePreferences` uses a hard-coded key. | Store credentials in **Windows Credential Manager** (DPAPI-backed, `PasswordVault` or CredRead). Exclude secrets from backups by default, or encrypt them with a user-supplied passphrase (AES-GCM, Argon2id KDF). |
| S6 | **Favorite identity relies on `(postid, md5)`.** Engines with synthetic md5 values (`md5(postUrl)` for Gelbooru 0.1.11, Shimmie RSS, BooruIO, e621 fallback) break dedupe across servers or URL changes. | Use `(server_id, remote_post_id)` as the primary identity and the real content hash as a secondary key. Optionally hash downloaded files (BLAKE3) to dedupe across servers. |
| S7 | **Favorites from Philomena, booru-on-rails and Hydrus cannot be saved locally** (`disableStorage`). | Remove that restriction; desktop storage is not constrained. |
| S8 | **Backup compatibility.** | Import `.abbj` (JSON) and the CSV format (§2.5), mapping `BackupServerItemType` codes to engine types, so existing users can migrate. Export a versioned JSON or zip archive with an optional encrypted secrets section. |

### 4.3 Grid, images and memory

| # | Mobile bottleneck | Desktop improvement |
|---|---|---|
| G1 | **RGB_565 thumbnails, `override(2048 or 4096)` downsampling, `QualityRender` forced to Low on every launch.** Quality is traded for low RAM. | Decode full 32-bit color with color management (ICC via WIC). Downsample only to the display size times the DPI scale. Keep originals for 1:1 zoom. Use GPU textures (Win2D or Skia) for pan and zoom. |
| G2 | **Column math assumes a phone** (`floor(widthDp/120)`), and true masonry exists only in Staggered mode with a fixed `grid_num_cols`. | Use a **justified or masonry layout** driven by known aspect ratios (`width` and `height` come back from every JSON engine), with a user-adjustable thumbnail size slider (Ctrl+wheel). Virtualize it (`ItemsRepeater` with a custom `VirtualizingLayout`) so 10k+ items stay smooth. Prefetch the viewport plus 2 screens. |
| G3 | **Thumbnails cached as DATA only, in a 1 GB cache with a crude cleanup** (`CacheCleanUpWorker` deletes up to 1200 old files per run). | Use a two-level cache: an in-memory decoded LRU sized to a percentage of RAM, plus an on-disk content-addressed cache (by URL hash) with LRU eviction to a user-set quota (for example 5 to 20 GB) and background compaction. |
| G4 | **Infinite scroll triggers at N−4 cells**, which is too late on a large screen showing 60+ cells. | Trigger by distance (within 1.5 viewport heights of the end) plus N2 prefetch. |
| G5 | **Blacklist evaluated with `String.contains` per rule per post** on the UI-bound path. | Precompile rules into hash sets: tag-ID sets with required and excluded bitsets. Evaluate during parsing on a worker thread. Show hidden-count chips with a reveal-on-hover option. |
| G6 | **Brittle per-host thumbnail failover** (the Gelbooru `img2→img3→img1` chain is hard-coded). | Make the host fallback list data-driven in quirk profiles (N6) and remember the last working mirror per host. |

### 4.4 Viewer and media

| # | Mobile bottleneck | Desktop improvement |
|---|---|---|
| V1 | **One shared video view** reparented between pages; the default player is a WebView `<video>` with a JS bridge (`video.html`). | Use one MediaPlayerElement (Media Foundation) or **libmpv** per visible pane with hardware decode (D3D11VA). Preload the first GOP of the next video. Support WebM/VP9/AV1 natively through mpv. Keep pages playing in multi-pane layouts. |
| V2 | **Preload count of 1, gated on Wi-Fi.** | Default to preloading 3 to 5 ahead and 1 behind, bandwidth-aware but not tied to the connection type. Cancel prefetch on fast scroll. |
| V3 | **Touch-only gesture model** (swipe up/down commands, volume-key paging, double-tap). | Full keyboard map (←/→, Home/End, F favorite, D download, O original, N notes, Space slideshow, Esc back), mouse-wheel zoom around the cursor, middle-click pan, and an optional filmstrip. Add a dual-pane or **multi-window viewer** and side-by-side comparison of parent and child posts. |
| V4 | **Notes are scaled once using `proportion`**, and the `Rect` is misused (§1.10). | Render notes as vector overlays in original-image coordinates, transformed with zoom and pan. Show the HTML body (sanitized) on hover. |
| V5 | **Animated GIFs are decoded entirely by Glide**, which uses a lot of memory for large GIFs. | Decode GIFs frame by frame with a frame-cache budget, or transcode large GIFs to video on the fly with mpv. Show APNG and animated WebP natively. |
| V6 | **The Danbooru `zip` (ugoira) workaround** falls back to the video sample. | Play ugoira natively from `frames` metadata, or keep the video sample. |

### 4.5 Downloads

| # | Mobile bottleneck | Desktop improvement |
|---|---|---|
| D1 | **Strictly sequential** single-thread executor; each file is fetched through Glide's disk cache and then copied, so it is written twice. | Use a parallel download manager (N concurrent per host, configurable) that streams directly to a temporary file and renames it atomically into place. Support HTTP range resume, a bandwidth limit, and pause/resume per item. |
| D2 | **Weak duplicate detection** by file name; the configurable naming mode omits the `.` before the extension (bug). | Name files from a template (`{artist} - {copyright} - {id}.{ext}` and similar, with tokens and length limits). Dedupe by remote ID and content hash. Optionally write tags into XMP or IPTC metadata, or a `.json`/`.txt` sidecar, so tools like Hydrus and digiKam can import them. |
| D3 | **SAF and MediaStore plumbing.** | Use plain filesystem paths with per-server or per-query folder rules, and optionally hard links into tag folders. Send Windows toast notifications when done. Add folder-watch export to Hydrus (`add_file`). |
| D4 | **No bulk "download whole query" with limits.** | Add a query-to-download job with a max count and date range, running in the background and resuming after restart. |

### 4.6 UX and platform

| # | Opportunity |
|---|---|
| U1 | **Tabs instead of the fragment back stack.** Each search, pin, pool or favorites filter opens in its own tab (the `SourceType.Permanent` map already models persistent sources). Restore the session on launch. |
| U2 | **Home pins become a dashboard** of resizable tiles, each a live mini-grid, and a sidebar tree for groups (`SourceSpecs` with children already supports nesting). |
| U3 | **Side panels that stay open:** the tag panel (InfoPanel) docks permanently. Tag colors come from the category cache. Click to search, Ctrl-click to add, Alt-click to exclude, right-click to blacklist. |
| U4 | **App lock that actually re-locks.** Lock on idle or minimize (timeout), using **Windows Hello** (`UserConsentVerifier`) or a PIN instead of the 3×3 gesture. Optional "boss key" that hides the window. Optionally blur thumbnails in the taskbar preview (DWM cloak). |
| U5 | **Theming:** follow the Windows light/dark setting and accent color (Mica or Acrylic), keep the Dark, Light, Gray and Purple presets, and switch themes live without restarting. |
| U6 | **Multi-select and batch actions:** rubber-band selection, Ctrl/Shift ranges, batch favorite, download, send to Hydrus, export CSV. |
| U7 | **Drag and drop:** drag images out to Explorer or other apps (saves the original). Drop a post URL or image onto the window to open it, or to reverse-search it on supported boorus (md5 or IQDB). |
| U8 | **Saved-search notifications:** a background service polls pinned queries and shows toast notifications with new-post counts. This replaces the Android widget, which is inactive here. |
| U9 | **Deep links:** register the `anibox://` protocol handler (`?server=&query=&user=`) for parity, plus a `booru://` handler for URL handoff from browsers. |
| U10 | **Diagnostics:** a network inspector panel (request, status, timing per engine) to replace the `LoggingInterceptor` logcat output. Useful when a booru changes its API. |

### 4.7 Suggested desktop architecture

```
App (WinUI 3 / Avalonia, MVVM)
 ├─ Core.Engines        IBooruEngine × 12 (+ quirk profiles JSON), typed DTOs, System.Text.Json source-gen, AngleSharp
 ├─ Core.Net            HttpClientFactory (per-host pools, token-bucket limiter, Polly retry, DoH, proxy, CookieContainer ⇄ WebView2)
 ├─ Core.Data           SQLite (WAL) + FTS5; repositories for Servers/Favorites/History/Blacklist/Pins/Downloads/TagCache
 ├─ Core.Media          Image pipeline (WIC/Skia decode → GPU textures, 2-level cache), libmpv video, note renderer
 ├─ Core.Downloads      Parallel resumable downloader, templated naming, sidecar/XMP writer, Hydrus exporter
 ├─ Core.Import         .abbj / CSV importer (BackupServerItemType mapping), CSV exporter
 └─ Security            Credential Manager store, Windows Hello lock, encrypted backup secrets
```

---

## Appendix A: Known bugs and quirks found during review

These are worth preserving or fixing on purpose in a port.

1. **`BooruProvider.setServerDescription`:** `host.split(".")` uses a regex, so the split result is always empty and `subdomain` stays as the full host. It is harmless because the value is unused.
2. **`ParserE621JSON`:** when meta tags exist, `sb3.append(asJsonObject3)` appends the file-metadata **JSON object** to `tags` instead of the meta tag string.
3. **`DanbooruPost.generateCurrentFileName`:** no `.` before the extension (for example `abc123 1234jpg`).
4. **`SourceQuery.setExtraTags("")`:** removes the key `""` instead of `extra_tags`, so a cleared sort option can persist.
5. **`UserConfiguration.setContext`:** the auto-detected `qualityRender` is immediately overwritten with `Low`.
6. **`PRELOAD_TYPE_DISABLED == PRELOAD_TYPE_ALWAYS == 1`.**
7. **`ParserIBSearchJSON`** is never selected by `Parser.fromProvider`, so IBSearch is effectively broken.
8. **ZeroChan and RSS** server types exist but have no URL generation.
9. **The note `Rect` is constructed as `(x, y, w, h)`.**
10. **Shimmie with an empty query** requests `/post/list//{page}`.
11. **`CustomPersistentCookieJar`** uses substring domain matching.
12. **The app lock is checked only at cold start.**
13. **The widget classes exist but are not registered in the manifest.**
14. **`FavoriteTask`** (network package) is a no-op stub.
15. **`ParserGelbooruXML.fixDanbooruPostURLs` / `fixImageURL`** are dead code.
