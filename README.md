# MediaLens

**Turn shared links into media.**

English | [简体中文](README.zh-CN.md)

MediaLens is a .NET 10 project for resolving public social-share text into structured media resources: videos, images, and live photos. Callers download the returned resources and save them to local storage or a photo library.

> **Status: project bootstrap.** This repository contains project documentation and shared development configuration. Platform support and a runnable API are planned; strictly cookie-free resolution has not yet been validated against live platforms.

## Goals

- Extract a supported share URL from pasted text and expand short links.
- Identify the platform, resource type, and work ID.
- Read publicly accessible metadata and return final CDN URLs with the request headers needed to download them.
- Preserve the order of album items and the image/video pairing of each live photo.
- Provide a small HTTP API with platform-specific parsing kept behind adapters.

The first validation target is public Douyin shares. Additional platforms will be evaluated individually; this is not a statement of current support.

## Cookie-free boundary

Cookie-free resolution is a project requirement, not an established platform capability:

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

## Planned API paths

These routes are a design contract, not an implemented API. The first platform adapter is Douyin (`douyin`); additional adapters require their own cookie-free feasibility verification.

| Method | Path | Purpose |
| --- | --- | --- |
| POST | `/api/v1/resolve` | Detect the platform from share text and resolve its media. |
| POST | `/api/v1/platforms/{platform}/resolve` | Resolve with an explicit platform constraint; Douyin uses `/api/v1/platforms/douyin/resolve`. |
| GET | `/api/v1/platforms` | List registered platforms, enablement, and verified media capabilities. |

Both POST routes accept the same `{ "text": "public share text" }` body and return the same media model. An explicit platform is a constraint, not permission to reinterpret a foreign-platform link or switch adapters. Platform discovery reads the in-memory adapter registry without contacting platforms or requiring a database.

The unified route remains stable as platforms are added. Platform identifiers are stable lowercase strings; media types do not become separate `/videos`, `/images`, or `/live-photos` routes. Compatibility is versioned at `/api/v1`; adding a platform alone does not require `/api/v2`.

Discovery distinguishes `unverified`, `supported`, and `unsupported` capabilities. Initially only Douyin is registered; unverified capabilities must not be advertised as supported. This status describes validated implementation coverage, not real-time upstream availability.

## Proposed result model

The two POST routes return the same platform-independent contract. This is a draft, not an implemented API; the example URL and headers are illustrative.

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
- The complete [response field design](openspec/changes/add-cookie-free-douyin-resolution/design.md) includes mixed-album examples, field types, nullability, and error semantics. Callers own local saving and native live-photo packaging/import.

## Technology

| Area | Choice |
| --- | --- |
| Language | C# |
| Development platform | .NET 10, targeting `net10.0` |
| Planned API host | ASP.NET Core 10 Minimal API |
| Recommended deployment | Docker with Linux containers |
| License | MIT |

## Development setup

Install a stable .NET 10.0 SDK and check it from the repository root:

```sh
dotnet --version
```

[`global.json`](global.json) selects SDK `10.0.100` or a later installed 10.0 feature band using `latestFeature`, with preview SDKs disabled. [`Directory.Build.props`](Directory.Build.props) defines the shared target framework, nullable reference types, implicit usings, and project metadata. See the [.NET SDK selection documentation](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json).

There is no application project to build or run at this stage. Add the runtime project and its verification tests after validating the initial anonymous resolution path.

## Docker deployment (recommended)

The planned deployment uses a multi-stage Docker build:

| Stage | Microsoft base image |
| --- | --- |
| Build and publish | `mcr.microsoft.com/dotnet/sdk:10.0` |
| Run the API | `mcr.microsoft.com/dotnet/aspnet:10.0` |

Use environment-based application configuration and container-standard logging. The resolver's core flow should not require persistent media storage. [`.dockerignore`](.dockerignore) excludes local build output, IDE state, and local configuration from the build context.

A Dockerfile, Compose configuration, and runnable deployment commands will be added with the API implementation. Refer to Microsoft's [ASP.NET Core container documentation](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/docker/building-net-docker-images?view=aspnetcore-10.0) for the base-image and multi-stage build approach.

## Initial milestones

- [ ] Verify cookie-free public Douyin video, image album, live-photo album, and mixed-album resolution.
- [ ] Implement the minimal API, normalized result model, and explicit failure responses.
- [ ] Verify final CDN downloads from the caller's network while preserving live-photo pairs.
- [ ] Add repeatable tests, Docker packaging, and deployment instructions.

## License

MediaLens is licensed under the [MIT License](LICENSE).
