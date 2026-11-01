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

All three routes require a single `Authorization: Bearer <token>` header, including platform discovery. Credentials are accepted only in this header. See [API access and runtime configuration](#api-access-and-runtime-configuration) for secret setup and deployment.

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

Errors have stable `code` values: `UNAUTHORIZED` uses 401 with `WWW-Authenticate: Bearer`; `INVALID_INPUT` and `PLATFORM_MISMATCH` use 400; `PLATFORM_NOT_FOUND` and confirmed `CONTENT_UNAVAILABLE` use 404; disabled platforms, anonymous unavailability, incomplete media, and unsupported resources use 422. `UPSTREAM_CHANGED` uses 502, temporary `UPSTREAM_UNAVAILABLE` uses 503, and `RESOLUTION_TIMEOUT` uses 504. Framework errors use the same response shape. Temporary failures may be retried; identity or input failures do not trigger a session fallback.

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
export SECRET_TOKEN="$(openssl rand -hex 32)"
dotnet run --project src/MediaLens.Api -- --urls http://127.0.0.1:8080
curl -H "Authorization: Bearer ${SECRET_TOKEN}" http://127.0.0.1:8080/api/v1/platforms
curl -X POST http://127.0.0.1:8080/api/v1/resolve \
  -H "Authorization: Bearer ${SECRET_TOKEN}" \
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
# For a first local run, generate a secret; reuse your configured secret on later starts.
export SECRET_TOKEN="$(openssl rand -hex 32)"
docker run --rm --read-only --tmpfs /tmp:rw,nosuid,size=32m \
  -e SECRET_TOKEN -p 127.0.0.1:8080:8080 media-lens:local
```

By default, platform/CDN hosts must resolve to real public addresses. If your development machine uses TUN Fake-IP DNS, configure real-address resolution for the relevant hosts; for Bilibili this includes bilibili.com, b23.tv, and bilivideo.com. For temporary diagnostics on a controlled network, the [runtime switch below](#api-access-and-runtime-configuration) can disable public IP checks. This does not configure proxy routing or guarantee that Fake-IP addresses are reachable. See [Douyin network conditions](docs/validation/cookie-free-douyin.md) and [Bilibili network conditions](docs/validation/cookie-free-bilibili.md).

The container runs as the base image's non-root user. No browser, database, Redis, media volume, or upstream account configuration is required. Set `MediaLens__ResolutionTimeoutSeconds` to an integer from 1 through 120 to change the total resolution deadline (default 30 seconds).

### API access and runtime configuration

**Breaking deployment change:** all three API endpoints, including platform discovery and monitoring requests, require `Authorization: Bearer <token>`. Startup fails with a nonzero exit code before listening if the effective secret is missing or invalid. There is no default secret, automatic generation, or anonymous fallback. Update callers and inject a secret when upgrading an existing container.

| Variable | Meaning | Validation/default | Other configuration entry points |
| --- | --- | --- | --- |
| `SECRET_TOKEN` | Shared API credential | Required; 32–256 ASCII characters, inclusive, using Bearer token syntax | `MediaLens__Auth__SecretToken` / `MediaLens:Auth:SecretToken` |
| `HTTP_PORT` | Container-internal HTTP port | One decimal integer, 1–65535; image default 8080 | Explicit URLs / Kestrel endpoints |
| `VALIDATE_PUBLIC_IP` | Validate upstream IP addresses as public | `true` / `false`, case-insensitive; default `true` | `MediaLens__Http__ValidatePublicIp` / `MediaLens:Http:ValidatePublicIp` |

These environment variable names are MediaLens conventions read and mapped by the application; Docker passes them into the container. The Dockerfile sets `ENV HTTP_PORT=8080`. Override `HTTP_PORT` at container startup to change the port, and inject the required `SECRET_TOKEN` at runtime. `HTTP_PORT` is the project's single port environment variable; the application does not add compatibility aliases for other port environment variables. Development `launchSettings.json` profiles are excluded from the build context.

Length counts the token text itself, without the `Bearer ` prefix or Base64 decoding. Non-ASCII, whitespace, malformed padding, and out-of-range values are rejected without trimming or truncation. Use a cryptographically random secret, such as 32 random bytes encoded as 64 hexadecimal characters or 43 unpadded Base64URL characters; length alone does not guarantee randomness. Secrets remain in the API layer and never enter upstream requests, response download headers, or logs. Invalid credentials reaching authentication return `401 / UNAUTHORIZED`, `WWW-Authenticate: Bearer`, and the existing `requestId + error` JSON before body binding or digest calculation for malformed values.

For the same configuration key, precedence is command line > public environment variable > native .NET environment variable (where supported) > configuration file > default. An invalid selected value fails instead of falling back. `HTTP_PORT` maps internally to the framework's `HTTP_PORTS` key; `--urls`, `ASPNETCORE_URLS`, and explicit Kestrel endpoints retain their higher-priority URL/endpoint behavior. The single-port validation applies when `HTTP_PORT` supplies the effective port setting. Standalone execution without listening settings retains the framework default; the Dockerfile supplies `HTTP_PORT=8080`.

To temporarily disable public IP checks, add `-e VALIDATE_PUBLIC_IP=false` to `docker run`, or set it in your `.env` passed with `--env-file`. The API applies the startup value to both Douyin and Bilibili metadata, short-link, and media connections.

Configuration is read at process startup. For standalone execution, restart the API process after changing its configuration. Docker environment variables are set when the container is created, so recreate the container with the updated `-e`/`--env-file` settings; for Compose, run `docker compose up -d` after updating the environment. Set `true` and apply the configuration the same way to restore checks.

Empty or invalid values, including `0`, `1`, and `yes`, fail before listening. Each startup with checks disabled emits one `Warning` log beginning with `Public IP validation is disabled`.

Allowed platform hosts may then resolve to loopback, private, reserved, or synthetic addresses, so use this mode only on a controlled diagnostic network. Host, HTTP(S), default-port, URL-credential, redirect, authentication, and cookie-free restrictions remain active. The Probe tool keeps its default public IP checks.

The command above reads `SECRET_TOKEN` from the shell environment. Alternatively, copy [`.env.example`](.env.example) to an ignored `.env`, replace the empty `SECRET_TOKEN=` placeholder with a generated secret locally, and replace `-e SECRET_TOKEN` with `--env-file .env`. Do not commit real credentials. Neither `docker run` nor `dotnet run` automatically loads `.env`. Compose uses `.env` for interpolation; pass values into the container through `environment` as below or through `env_file`:

```yaml
services:
  media-lens:
    image: media-lens:local
    environment:
      SECRET_TOKEN: "${SECRET_TOKEN:?Set SECRET_TOKEN before starting}"
      HTTP_PORT: "8080"
      VALIDATE_PUBLIC_IP: "${VALIDATE_PUBLIC_IP-true}"
    ports:
      - "127.0.0.1:8080:8080"
    read_only: true
    tmpfs:
      - /tmp:rw,nosuid,size=32m
```

Changing `HTTP_PORT` does not change Docker port publishing or `EXPOSE`. For example, `-e HTTP_PORT=9000 -p 127.0.0.1:8080:9000` forwards host port 8080 to container port 9000. For public access, terminate HTTPS at a trusted reverse proxy or deployment platform and keep the proxy-to-container connection controlled. A shared token proves possession, not a specific website or App; avoid embedding it in a public frontend, and do not treat CORS as API authentication. Rotate the secret by updating runtime configuration and restarting/recreating the service; the old secret then stops working.

Reproduce production-image checks after building with `python3 tools/validate-api-access-control.py`; only synthetic credentials are used and its temporary containers are removed. See the [access-control validation record](docs/validation/api-access-control.md) for boundary, regression, and Docker evidence and their scope.

For switch-specific container checks, run `python3 tools/validate-public-ip-validation.py --image media-lens:local`. See the [public IP validation record](docs/validation/public-ip-validation.md) for configuration, controlled-connection, and Docker coverage.

Image build and container checks passed, including discovery, both POST routes, Cookie rejection, and a read-only root filesystem without persistent writes. Real media checks and deployment checks are recorded separately. See the [Douyin container validation](docs/validation/container-validation.json) and [Bilibili API/container evidence](docs/validation/2026-10-31-bilibili-success.json).

## Initial milestones

- [x] Verify cookie-free public Douyin video, image album, live-photo album, and mixed-album resolution.
- [x] Implement the minimal API, normalized result model, and explicit failure responses.
- [x] Verify final CDN downloads from the caller's network while preserving live-photo pairs.
- [x] Add repeatable tests, Docker packaging, and deployment instructions.

## License

MediaLens is licensed under the [MIT License](LICENSE).
