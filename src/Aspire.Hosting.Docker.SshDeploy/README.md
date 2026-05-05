# Aspire.Hosting.Docker.SshDeploy

Deploy .NET Aspire applications to remote Docker hosts via SSH.

## Installation

1. Add the package feed:

```bash
dotnet nuget add source https://f.feedz.io/davidfowl/aspire/nuget/index.json --name davidfowl-aspire
```

2. Install the package:

```bash
aspire add docker-sshdeploy

# Or with the .NET CLI:
dotnet add package Aspire.Hosting.Docker.SshDeploy --prerelease
```

## TypeScript AppHosts

TypeScript AppHosts can use the same Docker SSH deployment APIs. Add the package to
`aspire.config.json` and regenerate `.modules` with `aspire restore` or `aspire add`:

```json
{
  "appHost": {
    "path": "apphost.ts",
    "language": "typescript/nodejs"
  },
  "packages": {
    "Aspire.Hosting.Docker.SshDeploy": "<package-version>"
  }
}
```

For local development against this repository, reference the project file instead:

```json
{
  "packages": {
    "Aspire.Hosting.Docker.SshDeploy": "../src/Aspire.Hosting.Docker.SshDeploy/Aspire.Hosting.Docker.SshDeploy.csproj"
  }
}
```

Then use the generated API from `.modules/aspire.js`:

```typescript
import { createBuilder, PullPolicy } from './.modules/aspire.js';

const builder = await createBuilder();

await builder.addDockerComposeEnvironment('env')
    .withSshDeploySupport()
    .withImagePullPolicy(PullPolicy.Never)
    .withPullRegistry('localhost:5001')
    .withAppFileTransfer('./certs', 'certs')
    .withFileTransfer('./config', '$HOME/config');

await builder.build().run();
```

See `samples/DockerPipelinesTypeScriptSample` in this repository for a complete TypeScript AppHost
sample that deploys the existing API and web projects.

## Usage Scenarios

### 1. Interactive Mode (Simplest)

Just add SSH deploy support and run `aspire deploy`. You'll be prompted for everything:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("env")
    .WithSshDeploySupport();

builder.Build().Run();
```

```bash
aspire deploy
```

The pipeline prompts for:
- Registry URL (e.g., `docker.io`, `ghcr.io`)
- Repository prefix (e.g., `myusername` or `myorg/myrepo`)
- SSH target host
- SSH credentials

No explicit registry login is performed - docker uses whatever credentials it already has.

---

### 2. Pre-configured Registry (No Prompts)

Configure the registry via environment variables or `appsettings.json` to skip prompts:

```csharp
builder.AddDockerComposeEnvironment("env")
    .WithSshDeploySupport();
```

**Environment variables:**
```bash
export DockerRegistry__RegistryUrl=docker.io
export DockerRegistry__RepositoryPrefix=myusername
```

**Or appsettings.json:**
```json
{
  "DockerRegistry": {
    "RegistryUrl": "docker.io",
    "RepositoryPrefix": "myusername"
  }
}
```

This assumes you're already logged in to the registry (e.g., via `docker login`).

---

### 3. With Registry Credentials

Add username and password to perform explicit `docker login` before pushing:

```csharp
builder.AddDockerComposeEnvironment("env")
    .WithSshDeploySupport();
```

**Environment variables:**
```bash
export DockerRegistry__RegistryUrl=ghcr.io
export DockerRegistry__RepositoryPrefix=myorg/myrepo
export DockerRegistry__RegistryUsername=myusername
export DockerRegistry__RegistryPassword=ghp_xxxxxxxxxxxx
```

**Or appsettings.json:**
```json
{
  "DockerRegistry": {
    "RegistryUrl": "ghcr.io",
    "RepositoryPrefix": "myorg/myrepo",
    "RegistryUsername": "myusername",
    "RegistryPassword": "ghp_xxxxxxxxxxxx"
  }
}
```

When both username and password are configured, the pipeline logs in to the registry before pushing images.

---

### 4. Custom Registry Resource (Full Control)

For complete control, define your own container registry resource:

```csharp
var registry = builder.AddContainerRegistry("my-registry", "ghcr.io", "myorg/myrepo");

// Option A: Already logged in (no credentials needed)
builder.AddDockerComposeEnvironment("env")
    .WithContainerRegistry(registry)
    .WithSshDeploySupport();
```

```csharp
// Option B: With explicit credentials
var username = builder.AddParameter("registry-username");
var password = builder.AddParameter("registry-password", secret: true);

var registry = builder.AddContainerRegistry("my-registry", "ghcr.io", "myorg/myrepo")
    .WithCredentialsLogin(username, password);

builder.AddDockerComposeEnvironment("env")
    .WithContainerRegistry(registry)
    .WithSshDeploySupport();
```

When you specify your own registry via `WithContainerRegistry()`, the built-in default registry is not used.

---

## SSH Configuration

### Environment Variables

```bash
export DockerSSH__TargetHost=your-server.com
export DockerSSH__SshUsername=deploy
export DockerSSH__SshKeyPath=~/.ssh/id_ed25519
```

### appsettings.json

**Key-based authentication (recommended):**
```json
{
  "DockerSSH": {
    "TargetHost": "your-server.com",
    "SshUsername": "deploy",
    "SshKeyPath": "~/.ssh/id_ed25519",
    "SshPassword": "key-passphrase-if-any"
  }
}
```

**Password-based authentication:**
```json
{
  "DockerSSH": {
    "TargetHost": "your-server.com",
    "SshUsername": "deploy",
    "SshPassword": "your-password"
  }
}
```

The `SshKeyPath` supports tilde and `$HOME` expansion:
```
~/.ssh/id_ed25519
$HOME/.ssh/id_rsa
/Users/john/.ssh/id_rsa
```

### Target Host Privacy

The target host is masked in output by default. To show it:
```bash
export UNSAFE_SHOW_TARGET_HOST=true
```

---

## Deployment Configuration

### Pull policy

The remote `docker compose up` step always passes `--pull always` by default, so registries are
re-checked on every deploy. Override either through code with `WithImagePullPolicy(...)` or through
configuration with `Deployment:PullPolicy`:

| Value     | Emitted command                                              | When to use                                                                 |
| --------- | ------------------------------------------------------------ | --------------------------------------------------------------------------- |
| `always`  | `docker compose up -d --pull always --remove-orphans`        | **Default.** Re-pull every deploy. Safest with multi-host registries.       |
| `missing` | `docker compose up -d --remove-orphans`                      | Only pull when the image isn't in the remote daemon's local store (Docker's compose default). |
| `never`   | `docker compose up -d --pull never --remove-orphans`         | Skip pulls entirely. Required for workflows that load images directly into the remote daemon (e.g. [unregistry](https://github.com/psviderski/unregistry)). |

**From AppHost.cs** (most discoverable, type-safe — naming matches `WithImagePullPolicy` on container resources elsewhere in Aspire):

```csharp
builder.AddDockerComposeEnvironment("env")
    .WithSshDeploySupport()
    .WithImagePullPolicy(PullPolicy.Never);
```

**From environment / appsettings** (override at deploy time without an AppHost rebuild):

```bash
export Deployment__PullPolicy=never
```

```json
{
  "Deployment": {
    "PullPolicy": "never"
  }
}
```

If both are set, `WithImagePullPolicy(...)` wins — explicit AppHost configuration overrides
environment-driven defaults, matching how `WithContainerRegistry(...)` overrides the config-derived
default registry. Unknown configuration values fall back to `always`.

> **Note on the enum type.** `WithImagePullPolicy(...)` takes this package's `PullPolicy` enum rather
> than `Aspire.Hosting.ApplicationModel.ImagePullPolicy`. The Aspire enum didn't include `Never`
> until 13.2; once this package's minimum Aspire version moves to 13.2+ we can adopt it directly.

### Separate push and pull endpoints (`WithPullRegistry`)

Some workflows push to one registry endpoint and want the remote to pull from a different one.
Two common shapes:

- **CI → registry mirror.** GitHub Actions pushes to `ghcr.io/myorg/...`, but the remote pulls
  from a private mirror at `mirror.example.com/myorg/...` that holds the promoted artifact.
- **Dev box → unregistry on the remote (via SSH tunnel).** The dev box pushes through an SSH
  tunnel to `host.docker.internal:5001`, but the remote daemon resolves the same image as
  `localhost:5001/...`. See
  [Local containerd-backed registry (unregistry)](#local-containerd-backed-registry-unregistry)
  below for the end-to-end setup.

`WithPullRegistry` swaps the registry prefix on each `*_IMAGE=` line written into the generated
`.env` file. The push side (`docker push`, `docker login`, image tagging) is untouched.

```csharp
builder.AddDockerComposeEnvironment("env")
    .WithContainerRegistry(builder.AddContainerRegistry("reg", "ghcr.io", "myorg"))
    .WithPullRegistry("mirror.example.com")  // remote resolves images here
    .WithSshDeploySupport();
```

Mechanism: it's a thin wrapper over Aspire's `ConfigureEnvFile` hook. During the prepare phase,
each captured environment variable whose name is `UPPER_SNAKE_CASE` ending in `_IMAGE` and whose
value contains a `/` has its first slash-delimited segment replaced with the configured pull
endpoint. Other variables are left alone. If the pull endpoint already matches, the call is a
no-op.

If you need finer control (per-resource overrides, different rewrites for different image
variables, conditional logic), call `ConfigureEnvFile` directly instead — `WithPullRegistry`
is just sugar over it.

---

## Local containerd-backed registry (unregistry)

[unregistry](https://github.com/psviderski/unregistry) is a tiny OCI registry that stores layers
in your Docker daemon's existing image store. The push side writes images straight into the
remote daemon — there's no separate blob storage — so no further pull is strictly necessary.

The two friction points it removes:

- **No public registry.** Push from your dev box (or CI) through SSH to a registry the rest of
  the internet can't see.
- **No re-pull at deploy time.** The image is already in the remote daemon's image store the
  moment the push completes.

### What you need on each side

- **Remote machine:** an unregistry instance listening on `localhost:5001`. The upstream
  README has the install one-liner.
- **Dev box / CI:** an SSH tunnel exposing the remote's port 5001 locally:

  ```bash
  ssh -L 5001:localhost:5001 user@remote
  ```

  With the tunnel up, `docker push localhost:5001/...` from your machine reaches the remote's
  unregistry through encrypted SSH.

- **Both daemons:** add the registry to `insecure-registries` so docker doesn't try HTTPS.
  unregistry serves plain HTTP (TLS would be redundant — the only path is loopback or SSH).

  Edit `~/.docker/daemon.json`:
  ```json
  {
    "insecure-registries": ["host.docker.internal:5001", "localhost:5001"]
  }
  ```

  Apply on both machines and restart the daemon. The dev box uses `host.docker.internal` because
  Docker Desktop's daemon can't reach the host's `localhost` directly; the remote uses
  `localhost` because unregistry runs there.

### Wire it up

```csharp
var registry = builder.AddContainerRegistry(
    "unregistry",
    "host.docker.internal:5001",      // push endpoint (dev box's view of the tunnel)
    "kaizen-tools");

builder.AddDockerComposeEnvironment("env")
    .WithContainerRegistry(registry)
    .WithPullRegistry("localhost:5001")  // pull endpoint (remote's view of itself)
    .WithSshDeploySupport();
```

By default the remote still issues `docker compose up -d --pull always`, which causes a
redundant round-trip back to unregistry — harmless once the registry endpoint is configured as
insecure on both daemons, but noisy. Pair with `WithImagePullPolicy(PullPolicy.Never)` or
`Deployment:PullPolicy=never` to skip that pull entirely, so the remote runs
`docker compose up -d --pull never --remove-orphans` against image refs that already exist
locally.

### Credentials

`WithPullRegistry` does not introduce a separate credentials channel — `docker login` on the
remote (when `DockerRegistry:RegistryUsername`/`RegistryPassword` are set) targets the **push**
endpoint only. unregistry doesn't require auth, so this matches the use case. Bring your own
credential plumbing if you need a different setup.

---

## File Transfer

Transfer additional files (certificates, configs, etc.) to the remote server.

### Relative to Deploy Path

```csharp
builder.AddDockerComposeEnvironment("env")
    .WithSshDeploySupport()
    .WithAppFileTransfer("./certs", "certs");  // ./certs → {RemoteDeployPath}/certs
```

### Absolute Path

```csharp
builder.AddDockerComposeEnvironment("env")
    .WithSshDeploySupport()
    .WithFileTransfer("./certs", "$HOME/certs");
```

Or with a parameter:
```csharp
var certDir = builder.AddParameter("certDir");

builder.AddDockerComposeEnvironment("env")
    .WithSshDeploySupport()
    .WithFileTransfer("./certs", certDir);
```

---

### 5. GitHub Actions with GHCR

Use GitHub Container Registry with the built-in `GITHUB_TOKEN`:

```csharp
builder.AddDockerComposeEnvironment("env")
    .WithSshDeploySupport();
```

**Environment variables (in workflow):**
```yaml
env:
  DockerSSH__TargetHost: ${{ secrets.TARGET_HOST }}
  DockerSSH__SshUsername: ${{ secrets.SSH_USERNAME }}
  DockerRegistry__RegistryUrl: ghcr.io
  DockerRegistry__RepositoryPrefix: ${{ github.repository }}
  DockerRegistry__RegistryUsername: ${{ github.actor }}
  DockerRegistry__RegistryPassword: ${{ secrets.GITHUB_TOKEN }}
```

**Required secrets:** `SSH_PRIVATE_KEY`, `SSH_USERNAME`, `TARGET_HOST`

The `GITHUB_TOKEN` is automatically available with `packages:write` permission.

---

## CI/CD with GitHub Actions

### Generate Workflow Automatically

```bash
aspire do gh-action-env
```

This creates a GitHub environment with secrets and generates `.github/workflows/deploy-env.yml`.

### Multiple Environments

```bash
aspire do gh-action-env -e staging
aspire do gh-action-env -e production
```

---

## Teardown

Stop and remove all containers:

```bash
aspire do teardown-env
```

This connects via SSH, shows running containers, and runs `docker compose down` after confirmation.

---

## Example Workflow

A complete GitHub Actions workflow for deploying to a remote server:

```yaml
name: Deploy

on:
  workflow_dispatch:
  push:
    branches: [main]

jobs:
  deploy:
    runs-on: ubuntu-latest
    environment: Production
    permissions:
      contents: read
      packages: write
    steps:
      - uses: actions/checkout@v6

      - name: Setup .NET
        uses: actions/setup-dotnet@v5
        with:
          dotnet-version: '10.0.x'

      - name: Install Aspire CLI
        run: curl -sSL https://aspire.dev/install.sh | bash

      - name: Setup SSH agent
        uses: webfactory/ssh-agent@v0.10.0
        with:
          ssh-private-key: ${{ secrets.SSH_PRIVATE_KEY }}

      - name: Add known hosts
        run: ssh-keyscan -H ${{ secrets.TARGET_HOST }} >> ~/.ssh/known_hosts

      - name: Deploy
        run: aspire deploy -e Production
        env:
          DockerSSH__TargetHost: ${{ secrets.TARGET_HOST }}
          DockerSSH__SshUsername: ${{ secrets.SSH_USERNAME }}
          DockerRegistry__RegistryUrl: ghcr.io
          DockerRegistry__RepositoryPrefix: ${{ github.repository }}
          DockerRegistry__RegistryUsername: ${{ github.actor }}
          DockerRegistry__RegistryPassword: ${{ secrets.GITHUB_TOKEN }}
```

---

## Links

- [GitHub Repository](https://github.com/davidfowl/AspirePipelines)
- [Sample Project](https://github.com/davidfowl/AspirePipelines/tree/main/samples/DockerPipelinesSample)
