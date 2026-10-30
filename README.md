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

## Proposed result model

This example describes the intended data shape, not an implemented API contract. URLs are illustrative.

```json
{
  "platform": "douyin",
  "workId": "123456",
  "workType": "album",
  "items": [
    {
      "type": "image",
      "imageUrls": ["https://cdn.example/image-01.jpg"]
    },
    {
      "type": "livePhoto",
      "imageUrls": ["https://cdn.example/image-02.jpg"],
      "videoUrls": ["https://cdn.example/image-02.mp4"]
    }
  ],
  "requestHeaders": {
    "User-Agent": "<download user agent>",
    "Referer": "https://www.douyin.com/"
  },
  "expiresAt": null
}
```

- `workType` distinguishes `video` and `album`; each item is a `video`, `image`, or `livePhoto`. An album can contain both still images and live photos.
- A video item uses `videoUrls`. A live-photo item keeps its still image and motion clip together.
- Preserve complete URLs, including their query parameters. These addresses may be temporary; callers should resolve the share again when an address expires.
- Download headers must not contain cookies or upstream account credentials. Use resource-specific headers if a platform requires different values for different resources.
- Use `null` when expiration is unknown; do not invent an expiry time. Final CDN delivery must also be verified from the caller's network.

## Technology

| Area | Choice |
| --- | --- |
| Language | C# |
| Development platform | .NET 10, targeting `net10.0` |
| Planned API host | ASP.NET Core 10 |
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
