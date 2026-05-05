# Docker Pipelines TypeScript Sample

This sample uses a TypeScript Aspire AppHost to deploy the existing sample API and web projects with `Aspire.Hosting.Docker.SshDeploy`.

```bash
npm install
aspire restore
npm run build
aspire deploy
```

The AppHost references the local `Aspire.Hosting.Docker.SshDeploy.csproj`, so changes to the hosting integration are reflected after `aspire restore`.
