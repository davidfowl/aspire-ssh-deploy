// Sample: SSH deployment from a TypeScript AppHost.
// Demonstrates: generated TypeScript APIs for Aspire.Hosting.Docker.SshDeploy.

import { createBuilder, PullPolicy } from './.modules/aspire.js';

const builder = await createBuilder();

await builder.addDockerComposeEnvironment('env')
    .withSshDeploySupport()
    .withImagePullPolicy(PullPolicy.Missing)
    .withAppFileTransfer('./config', 'config');

const cache = await builder.addRedis('cache');

const apiService = await builder.addProject(
    'apiservice',
    '../DockerPipelinesSample/DockerPipelinesSample.ApiService/DockerPipelinesSample.ApiService.csproj')
    .withHttpHealthCheck({ path: '/health' });

await builder.addProject(
    'webfrontend',
    '../DockerPipelinesSample/DockerPipelinesSample.Web/DockerPipelinesSample.Web.csproj')
    .withHttpHealthCheck({ path: '/health' })
    .withReference(cache)
    .waitFor(cache)
    .withReference(apiService)
    .waitFor(apiService)
    .withEnvironment('IMAGE_TAG_SUFFIX', process.env.IMAGE_TAG_SUFFIX ?? 'local')
    .withExternalHttpEndpoints();

await builder.build().run();
