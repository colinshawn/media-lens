# MediaLens

**Turn shared links into media.**

English | [简体中文](README.zh-CN.md)

MediaLens is a .NET 10 project for resolving public social-share text into structured media resources: videos, images, and live photos. Callers download the returned resources and save them to local storage or a photo library.

> **Status: Douyin media resolution and Bilibili public UGC MP4 resolution are supported.** Both platforms remain strictly cookie-free. See the [Douyin validation](docs/validation/cookie-free-douyin.md) for four media types and the [Bilibili validation](docs/validation/cookie-free-bilibili.md) for BV/AV, short links, selected parts, complete audio/video downloads, and network conditions.

## Goals

- Extract a supported share URL from pasted text and expand short links.
- Identify the platform, resource type, and work ID.
- Read publicly accessible metadata and return final CDN URLs with the request headers needed to download them.
- Preserve the order of album items and the image/video pairing of each live photo.
- Provide a small HTTP API with platform-specific parsing kept behind adapters.

Douyin public shares and Bilibili public UGC videos are supported. Additional platforms require their own adapter and anonymous validation.

## Cookie-free boundary

The implementation keeps the following cookie-free boundary:

- Do not accept, generate, store, or send cookies. Do not forward incoming `Cookie` headers or reuse upstream `Set-Cookie` values, including across redirects.
- Do not create account or guest identities, maintain identity pools, or use browser sessions as a fallback.
- Ordinary HTTP headers and local signature calculations may be used when they do not require identity or session construction.
- Return an explicit unsupported or unavailable result when an anonymous flow cannot supply the media. Do not silently enable a cookie-based fallback.

A public share being viewable does not establish that its metadata can be retrieved without a session. Cookie-free feasibility must be verified per platform and media type before support is advertised.

## Resolution flow

```text
Share text
  -> Extract URL and expand short links
  -> Identify platform and work ID
  -> Request metadata without cookies
  -> Normalize video, image, and live-photo resources
  -> Resolve playback redirects to final CDN URLs
  -> Return structured media information
```

The resolver returns addresses and metadata. Callers own media downloads, local file storage, photo-library permissions, and any native live-photo packaging or import.

## Supported platforms

| Platform | Video | Image albums | Live / mixed albums |
| --- | --- | --- | --- |
| Douyin | Supported | Supported | Supported |
| Bilibili | Complete single-file MP4 for ordinary public UGC | Not implemented | Not implemented |

Bilibili accepts BV/AV video URLs and b23.tv short links. AV input is normalized to the verified BV identity. Each request resolves one part: part 1 by default, or the positive `p` parameter in the shared URL. Invalid or out-of-range values fail explicitly. The JSON body still contains only text; there are no extra part routes or batch semantics.

Bilibili workId identifies the upload's BV, while webUrl preserves the selected p and suggested filenames include the part. Different parts of one upload can share workId; preserve the part when saving or caching resources.

Only complete MP4 files preserving the source audio are returned. DASH tracks, sequential segments, FLV/HLS, previews, bangumi, live streams, interactive videos, and image posts fail explicitly. Anonymous quality depends on the actual upstream response; requesting a qn does not guarantee it. Unknown candidate dimensions remain null. The verified samples returned quality=64; that is not a promise for every work.

## HTTP API

These routes use ASP.NET Core 10 Minimal API. Both `douyin` and `bilibili` are registered and enabled, using the same routes, static DTOs, and error contract.

| Method | Path | Purpose |
| --- | --- | --- |
| POST | `/api/v1/resolve` | Detect the platform from share text and resolve its media. |
| POST | `/api/v1/platforms/{platform}/resolve` | Resolve with an explicit platform constraint, such as `/api/v1/platforms/douyin/resolve` or `/api/v1/platforms/bilibili/resolve`. |
| GET | `/api/v1/platforms` | List registered platforms, enablement, and verified media capabilities. |

Both POST routes accept the same `{ "text": "public share text" }` body and return the same media model. An explicit platform is a constraint, not permission to reinterpret a foreign-platform link or switch adapters. Platform discovery reads the in-memory adapter registry without contacting platforms or requiring a database.

The unified route remains stable as platforms are added. Platform identifiers are stable lowercase strings; media types do not become separate `/videos`, `/images`, or `/live-photos` routes. Compatibility is versioned at `/api/v1`; adding a platform alone does not require `/api/v2`.

Discovery distinguishes `unverified`, `supported`, and `unsupported` capabilities. Douyin supports all four media categories. Bilibili supports video; its other three categories are `unsupported`. Unverified capabilities must not be advertised as supported. This status describes validated implementation coverage, not real-time upstream availability.

## Response contract

The two POST routes share the same implemented contract. The success example below uses synthetic data and contains no usable download URL.

```json
{
  "requestId": "example-request-video",
  "platform": "douyin",
  "workId": "123456",
  "workType": "video",
  "webUrl": "https://www.douyin.com/video/123456",
  "description": "公开视频示例",
  "items": [
    {
      "index": 1,
      "type": "video",
      "imageSources": [],
      "videoSources": [
        {
          "url": "https://cdn.example/01_video.mp4",
          "headers": {
            "User-Agent": "<download user agent>",
            "Referer": "https://www.douyin.com/"
          },
          "mimeType": "video/mp4",
          "fileName": "douyin_123456_01_video.mp4",
          "width": 1080,
          "height": 1920,
          "durationMs": 12000,
          "sizeBytes": null,
          "expiresAt": null
        }
      ]
    }
  ],
  "resolvedAt": "2026-10-30T08:00:00Z",
  "expiresAt": null
}
```

- Every response includes a server-generated `requestId`; a work is identified by `(platform, workId)`, with IDs serialized as strings.
- `workType` is `video` or `album`. Items have contiguous one-based `index` values and a `type` of `video`, `image`, or `livePhoto`.
- `imageSources` and `videoSources` contain ordered fallback candidates for the same logical image or clip. Download one candidate from each required list, not every candidate. An unused list is `[]`.
- A live-photo item keeps both its image candidates and motion-clip candidates together. Albums preserve the original item order, including mixed still/live albums.
- Each source carries its complete `url`, its own `headers`, and nullable `mimeType`, `fileName`, `width`, `height`, `durationMs`, `sizeBytes`, and `expiresAt`. There are no inherited download headers. Source variants may have different formats or dimensions.
- Preserve complete URLs and all query parameters. Download headers never include cookies or upstream account credentials.
- Unknown metadata is explicitly `null`; image duration is `null`. The top-level `expiresAt` is the earliest source expiry only when every returned source has a known expiry; otherwise it is `null`.
- Error responses use `{ "requestId": "...", "error": { "code": "...", "message": "...", "retryable": false, "retryAfterSeconds": null } }`. Platform discovery returns `requestId` and `platforms`.
- The complete [response field design](openspec/changes/archive/2026-10-31-add-cookie-free-douyin-resolution/design.md) includes mixed-album examples, field types, nullability, and error semantics. Callers own local saving and native live-photo packaging/import.

Download using both the candidate's `url` and `headers`. Opening only the URL in a browser may return 403 when a required Referer or other header is missing. If a candidate fails, try the next source in order.

CDN addresses may expire. Re-resolve the original share if a download fails because an address has expired, preserve every query parameter, and use each candidate's own headers. Unknown expiry stays `null`; it is not a guarantee of permanent access.

Errors have stable `code` values: `INVALID_INPUT` and `PLATFORM_MISMATCH` use 400; `PLATFORM_NOT_FOUND` and confirmed `CONTENT_UNAVAILABLE` use 404; disabled platforms, anonymous unavailability, incomplete media, and unsupported resources use 422. `UPSTREAM_CHANGED` uses 502, temporary `UPSTREAM_UNAVAILABLE` uses 503, and `RESOLUTION_TIMEOUT` uses 504. Framework errors use the same response shape. Temporary failures may be retried; identity or input failures do not trigger a session fallback.

## Technology

| Area | Choice |
| --- | --- |
| Language | C# |
| Development platform | .NET 10, targeting `net10.0` |
| API host | ASP.NET Core 10 Minimal API |
| Recommended deployment | Docker with Linux containers |
| License | MIT |

## Development setup

Install a stable .NET 10.0 SDK and check it from the repository root:

```sh
dotnet --version
```

[`global.json`](global.json) selects SDK `10.0.100` or a later installed 10.0 feature band using `latestFeature`, with preview SDKs disabled. [`Directory.Build.props`](Directory.Build.props) defines the shared target framework, nullable reference types, implicit usings, and project metadata. See the [.NET SDK selection documentation](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json).

Build and test the verification tool from the repository root:

```sh
dotnet restore MediaLens.slnx
dotnet build MediaLens.slnx --no-restore
dotnet test MediaLens.slnx --no-build
dotnet format MediaLens.slnx --no-restore --verify-no-changes
```

`src/MediaLens.Api` hosts the API; `src/MediaLens.Core` contains contracts, registry, adapters, and the cookie-free HTTP transport shared with the probe. The two test projects use synthetic inputs, local TCP upstreams, and real Kestrel hosts to verify cookie isolation, media pairing, routing, serialization, errors, cancellation, expiry handling, and report privacy.

Run the API locally:

```sh
dotnet run --project src/MediaLens.Api -- --urls http://127.0.0.1:8080
curl http://127.0.0.1:8080/api/v1/platforms
curl -X POST http://127.0.0.1:8080/api/v1/resolve \
  -H 'Content-Type: application/json' \
  -d '{"text":"https://www.douyin.com/video/123456"}'
```

Discovery reports `douyin` and `bilibili`, both with `enabled: true`, and their individual capabilities. Both POST routes return media results for accessible works; replace the synthetic ID above with public share text. Incoming Cookie headers and extra identity fields are rejected. The service has no database, media persistence, browser, or session setup.

```sh
dotnet run --project tools/MediaLens.Probe -- \
  --input /absolute/path/outside-repo/samples.private.json --entry share
```

The input is an array of `{ "kind": "video", "text": "share text" }`; allowed categories are `video`, `imageAlbum`, `livePhoto`, and `mixedAlbum`. Entry modes are `share`, `mobile`, `desktop`, `detail`, `legacy`, and `adapter`. The last mode invokes the shared production adapter directly. Reports omit share text, URLs, work IDs, raw responses, and cookie values. Keep real samples and local output outside the repository; fixtures contain synthetic data only.

Bilibili uses the same external input format with kind=video. The default platform is douyin. Bilibili defaults to the adapter entry; it can also be explicit:

```sh
dotnet run --project tools/MediaLens.Probe -- \
  --platform bilibili --entry adapter \
  --input /absolute/path/outside-repo/bilibili.private.json
```

Other diagnostic entries remain Douyin-specific. Reports omit real shares, BV/AID/CID, signed queries, and complete media URLs. The tool validates metadata; complete downloads require a separate client.

Exit code `0` means complete metadata was observed for every input, **not** that caller downloads passed; `1` means at least one observation did not pass, and `2` means invalid input. The [validation record](docs/validation/cookie-free-douyin.md) documents results, DNS mapping constraints, and the scope of the success evidence.

## Docker deployment (recommended)

The Dockerfile uses a multi-stage Linux build:

| Stage | Microsoft base image |
| --- | --- |
| Build and publish | `mcr.microsoft.com/dotnet/sdk:10.0` |
| Run the API | `mcr.microsoft.com/dotnet/aspnet:10.0` |

Use environment-based application configuration and container-standard logging. The resolver's core flow should not require persistent media storage. [`.dockerignore`](.dockerignore) excludes local build output, IDE state, and local configuration from the build context.

```sh
docker build -t media-lens:local .
docker run --rm --read-only --tmpfs /tmp:rw,nosuid,size=32m \
  -p 127.0.0.1:8080:8080 media-lens:local
```

If your development machine uses TUN Fake-IP DNS, platform/CDN hosts must resolve to real public addresses. For Bilibili this includes the relevant bilibili.com, b23.tv, and bilivideo.com hosts. Synthetic, private, and reserved addresses remain rejected. See [Douyin network conditions](docs/validation/cookie-free-douyin.md) and [Bilibili network conditions](docs/validation/cookie-free-bilibili.md). Diagnostic DNS or hosts mappings are not deployment configuration.

The container runs as the base image's non-root user. No browser, database, Redis, media volume, or account configuration is required. Set `MediaLens__ResolutionTimeoutSeconds` to an integer from 1 through 120 to change the total resolution deadline (default 30 seconds). Binding uses the standard ASP.NET Core environment settings, such as `ASPNETCORE_HTTP_PORTS` (8080 in the image).

Image build and container checks passed, including discovery, both POST routes, Cookie rejection, and a read-only root filesystem without persistent writes. Real media checks and deployment checks are recorded separately. See the [Douyin container validation](docs/validation/container-validation.json) and [Bilibili API/container evidence](docs/validation/2026-10-31-bilibili-success.json).

## Initial milestones

- [x] Verify cookie-free public Douyin video, image album, live-photo album, and mixed-album resolution.
- [x] Implement the minimal API, normalized result model, and explicit failure responses.
- [x] Verify final CDN downloads from the caller's network while preserving live-photo pairs.
- [x] Add repeatable tests, Docker packaging, and deployment instructions.

## License

MediaLens is licensed under the [MIT License](LICENSE).
