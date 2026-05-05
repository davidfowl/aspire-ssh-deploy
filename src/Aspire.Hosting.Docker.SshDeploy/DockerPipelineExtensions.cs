#pragma warning disable ASPIREPIPELINES004
#pragma warning disable ASPIRECOMPUTE003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;
using Aspire.Hosting.Docker.SshDeploy.Abstractions;
using Aspire.Hosting.Docker.SshDeploy.Infrastructure;
using Aspire.Hosting.Docker.SshDeploy.Services;
using Aspire.Hosting.Docker.SshDeploy.Utilities;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Represents a file transfer mapping from a local path to a remote path on the deployment target.
/// </summary>
/// <param name="localPath">The local path (relative to AppHost directory) containing files to transfer.</param>
/// <param name="remotePath">The remote path provider that resolves to the destination directory.</param>
/// <param name="isRelativeToDeployPath">If true, the remote path is relative to RemoteDeployPath; otherwise it's an absolute path.</param>
public class FileTransferAnnotation(string localPath, IValueProvider remotePath, bool isRelativeToDeployPath) : IResourceAnnotation
{
    /// <summary>
    /// Gets the local path containing files to transfer.
    /// </summary>
    public string LocalPath { get; } = localPath;

    /// <summary>
    /// Gets the value provider for the remote destination path.
    /// </summary>
    public IValueProvider RemotePath { get; } = remotePath;

    /// <summary>
    /// Gets whether the remote path is relative to the deployment path.
    /// </summary>
    public bool IsRelativeToDeployPath { get; } = isRelativeToDeployPath;
}


/// <summary>
/// Carries the <see cref="PullPolicy"/> selected via
/// <see cref="DockerPipelineExtensions.WithImagePullPolicy(IResourceBuilder{DockerComposeEnvironmentResource}, PullPolicy)"/>.
/// When present on a <see cref="DockerComposeEnvironmentResource"/>, the deploy pipeline uses this
/// value in preference to the <c>Deployment:PullPolicy</c> configuration string.
/// </summary>
/// <param name="policy">The pull policy to apply.</param>
public class ImagePullPolicyAnnotation(PullPolicy policy) : IResourceAnnotation
{
    /// <summary>
    /// Gets the configured pull policy.
    /// </summary>
    public PullPolicy Policy { get; } = policy;
}

/// <summary>
/// Provides extension methods for adding Docker SSH pipeline resources to a distributed application.
/// </summary>
public static class DockerPipelineExtensions
{
    /// <summary>
    /// Adds SSH deployment support to a Docker Compose environment resource, enabling deployment 
    /// of containerized applications to remote Docker hosts via SSH.
    /// This deployment pipeline is only active during publish mode and provides an interactive configuration
    /// experience for SSH connection settings and deployment targets.
    /// </summary>
    /// <param name="resourceBuilder">The Docker Compose environment resource builder.</param>
    /// <returns>The resource builder for method chaining.</returns>
    /// <remarks>
    /// The SSH deployment pipeline allows deploying Docker containers to remote hosts via SSH.
    /// It provides an interactive setup during publish that prompts for:
    /// - Target server hostname or IP address
    /// - SSH authentication credentials (username, password, or key-based authentication)
    /// - Remote deployment directory
    /// - SSH connection settings
    /// 
    /// This deployment pipeline is only active when publishing the application and has no effect during local development.
    /// </remarks>
    [AspireExport("withSshDeploySupport", Description = "Adds SSH deployment support to a Docker Compose environment resource.")]
    public static IResourceBuilder<DockerComposeEnvironmentResource> WithSshDeploySupport(
        this IResourceBuilder<DockerComposeEnvironmentResource> resourceBuilder)
    {
        var builder = resourceBuilder.ApplicationBuilder;

        // Register infrastructure services (shared across all environments)
        builder.Services.TryAddSingleton<IProcessExecutor, ProcessExecutor>();
        builder.Services.TryAddSingleton<IFileSystem, FileSystemAdapter>();
        builder.Services.TryAddSingleton<DockerCommandExecutor>();
        builder.Services.TryAddSingleton<SSHConfigurationDiscovery>();
        builder.Services.TryAddSingleton<GitHubActionsGeneratorService>();
        builder.Services.TryAddSingleton<ISshKeyDiscoveryService, SshKeyDiscoveryService>();

        // Register both SSH connection factory implementations
        builder.Services.TryAddSingleton<NativeSSHConnectionFactory>();
        builder.Services.TryAddSingleton<SSHNetConnectionFactory>();

        // Register the ISSHConnectionFactory interface - selects native ssh by default, SSH.NET as fallback
        builder.Services.TryAddSingleton<ISSHConnectionFactory>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var useLegacy = config.GetValue<bool>("DockerSSH:UseLegacySshNet", false);

            if (useLegacy)
            {
                return sp.GetRequiredService<SSHNetConnectionFactory>();
            }

            return sp.GetRequiredService<NativeSSHConnectionFactory>();
        });

        // Register DockerSSHPipeline as a keyed service (one per resource)
        builder.Services.AddKeyedSingleton(
            resourceBuilder.Resource,
            (sp, _) => new DockerSSHPipeline(
                resourceBuilder.Resource,
                sp.GetRequiredService<DockerCommandExecutor>(),
                sp.GetRequiredService<IPipelineOutputService>(),
                sp.GetRequiredService<ISSHConnectionFactory>(),
                sp.GetRequiredService<GitHubActionsGeneratorService>(),
                sp.GetRequiredService<ISshKeyDiscoveryService>(),
                sp.GetRequiredService<IProcessExecutor>(),
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<IHostEnvironment>(),
                sp.GetRequiredService<ILoggerFactory>()));

        // Only configure registry in publish mode to avoid prompts during run mode
        if (builder.ExecutionContext.IsPublishMode)
        {
            // Create default registry with parameters upfront
            var config = builder.Configuration;
            var registryUrlConfig = config["DockerRegistry:RegistryUrl"];
            var repositoryPrefixConfig = config["DockerRegistry:RepositoryPrefix"];
            var registryUsername = config["DockerRegistry:RegistryUsername"];
            var registryPassword = config["DockerRegistry:RegistryPassword"];

            var registryUrlParam = string.IsNullOrEmpty(registryUrlConfig)
                ? builder.AddParameter($"registryUrl")
                : builder.AddParameter($"registryUrl", registryUrlConfig);

            var repositoryPrefixParam = string.IsNullOrEmpty(repositoryPrefixConfig)
                ? builder.AddParameter($"repositoryPrefix")
                : builder.AddParameter($"repositoryPrefix", repositoryPrefixConfig);

            var defaultRegistry = builder.AddContainerRegistry(
                $"default-registry-{resourceBuilder.Resource.Name}",
                registryUrlParam,
                repositoryPrefixParam);

            // Add credentials login only if both username and password are configured
            IResourceBuilder<ParameterResource>? usernameParam = null;
            IResourceBuilder<ParameterResource>? passwordParam = null;
            if (!string.IsNullOrEmpty(registryUsername) && !string.IsNullOrEmpty(registryPassword))
            {
                usernameParam = builder.AddParameter($"default-registry-username-{resourceBuilder.Resource.Name}", registryUsername);
                passwordParam = builder.AddParameter($"default-registry-password-{resourceBuilder.Resource.Name}", registryPassword, secret: true);

                defaultRegistry.WithCredentialsLogin(usernameParam, passwordParam);
            }

            // Subscribe to BeforeStartEvent to attach or remove default registry based on user configuration
            var dockerEnvResource = resourceBuilder.Resource;
            builder.Eventing.Subscribe<BeforeStartEvent>((@event, ct) =>
            {
                // Check if user already attached a registry via WithContainerRegistry()
                if (dockerEnvResource.TryGetLastAnnotation<ContainerRegistryReferenceAnnotation>(out _))
                {
                    // User specified their own registry - remove our default resources
                    builder.Resources.Remove(defaultRegistry.Resource);
                    builder.Resources.Remove(registryUrlParam.Resource);
                    builder.Resources.Remove(repositoryPrefixParam.Resource);
                    if (usernameParam != null) builder.Resources.Remove(usernameParam.Resource);
                    if (passwordParam != null) builder.Resources.Remove(passwordParam.Resource);
                    return Task.CompletedTask;
                }

                // Attach the default registry
                dockerEnvResource.Annotations.Add(new ContainerRegistryReferenceAnnotation(defaultRegistry.Resource));

                return Task.CompletedTask;
            });
        }

        return resourceBuilder.WithPipelineStepFactory(context =>
        {
            var pipeline = context.PipelineContext.Services.GetRequiredKeyedService<DockerSSHPipeline>(resourceBuilder.Resource);
            return pipeline.CreateSteps(context);
        })
        .WithPipelineConfiguration(context =>
        {
            var pipeline = context.Services.GetRequiredKeyedService<DockerSSHPipeline>(resourceBuilder.Resource);
            return pipeline.ConfigurePipelineAsync(context);
        });
    }

    /// <summary>
    /// Configures files to be transferred to the remote deployment directory via SCP.
    /// The remote path is relative to the configured RemoteDeployPath.
    /// </summary>
    /// <param name="builder">The Docker Compose environment resource builder.</param>
    /// <param name="localPath">The local path (relative to AppHost directory) containing files to transfer.</param>
    /// <param name="remoteSubPath">The subdirectory within RemoteDeployPath where files will be transferred.</param>
    /// <returns>The resource builder for method chaining.</returns>
    [AspireExport("withAppFileTransfer", Description = "Transfers files to a path relative to the remote deployment directory.")]
    public static IResourceBuilder<DockerComposeEnvironmentResource> WithAppFileTransfer(
        this IResourceBuilder<DockerComposeEnvironmentResource> builder,
        string localPath,
        string remoteSubPath)
    {
        builder.Resource.Annotations.Add(new FileTransferAnnotation(localPath, ReferenceExpression.Create($"{remoteSubPath}"), isRelativeToDeployPath: true));
        return builder;
    }

    /// <summary>
    /// Configures files to be transferred to an absolute path on the remote deployment target via SCP.
    /// The remote directory will be created if it doesn't exist.
    /// </summary>
    /// <param name="builder">The Docker Compose environment resource builder.</param>
    /// <param name="localPath">The local path (relative to AppHost directory) containing files to transfer.</param>
    /// <param name="remotePath">A parameter resource that resolves to the absolute remote destination directory.</param>
    /// <returns>The resource builder for method chaining.</returns>
    [AspireExportIgnore(Reason = "The string overload is exported for TypeScript AppHosts. Parameter-backed remote paths are a C# convenience overload.")]
    public static IResourceBuilder<DockerComposeEnvironmentResource> WithFileTransfer(
        this IResourceBuilder<DockerComposeEnvironmentResource> builder,
        string localPath,
        IResourceBuilder<ParameterResource> remotePath)
    {
        builder.Resource.Annotations.Add(new FileTransferAnnotation(localPath, remotePath.Resource, isRelativeToDeployPath: false));
        return builder;
    }

    /// <summary>
    /// Configures files to be transferred to an absolute path on the remote deployment target via SCP.
    /// The remote directory will be created if it doesn't exist.
    /// </summary>
    /// <param name="builder">The Docker Compose environment resource builder.</param>
    /// <param name="localPath">The local path (relative to AppHost directory) containing files to transfer.</param>
    /// <param name="remotePath">The absolute remote destination directory path (supports $HOME and ~ expansion).</param>
    /// <returns>The resource builder for method chaining.</returns>
    [AspireExport("withFileTransfer", Description = "Transfers files to an absolute path on the remote deployment target.")]
    public static IResourceBuilder<DockerComposeEnvironmentResource> WithFileTransfer(
        this IResourceBuilder<DockerComposeEnvironmentResource> builder,
        string localPath,
        string remotePath)
    {
        builder.Resource.Annotations.Add(new FileTransferAnnotation(localPath, ReferenceExpression.Create($"{remotePath}"), isRelativeToDeployPath: false));
        return builder;
    }

    /// <summary>
    /// Sets the image pull policy used when the deploy pipeline runs <c>docker compose up</c> on
    /// the remote. Wins over the <c>Deployment:PullPolicy</c> configuration string when both are
    /// set. Method name matches the <c>WithImagePullPolicy</c> convention used elsewhere in Aspire.
    /// </summary>
    /// <param name="builder">The Docker Compose environment resource builder.</param>
    /// <param name="policy">The pull policy to apply on the remote.</param>
    /// <returns>The resource builder for method chaining.</returns>
    /// <remarks>
    /// Mirrors the <c>Deployment:PullPolicy</c> config key but is more discoverable from AppHost.cs
    /// and naturally pairs with
    /// <see cref="WithPullRegistry(IResourceBuilder{DockerComposeEnvironmentResource}, string)"/>.
    /// Takes <see cref="PullPolicy"/> rather than <c>Aspire.Hosting.ApplicationModel.ImagePullPolicy</c>
    /// because the latter does not include <c>Never</c> until Aspire 13.2.
    /// </remarks>
    [AspireExport("withImagePullPolicy", Description = "Sets the image pull policy used for remote docker compose deployments.")]
    public static IResourceBuilder<DockerComposeEnvironmentResource> WithImagePullPolicy(
        this IResourceBuilder<DockerComposeEnvironmentResource> builder,
        PullPolicy policy)
    {
        builder.Resource.Annotations.Add(new ImagePullPolicyAnnotation(policy));
        return builder;
    }

    /// <summary>
    /// Configures the registry endpoint the <em>remote</em> daemon should pull images from, when it
    /// differs from the endpoint <c>docker push</c> sends layers to. Image references in the
    /// generated <c>.env</c> file are rewritten so the registry prefix on each <c>*_IMAGE</c>
    /// variable is replaced with <paramref name="endpoint"/>. The push side (<c>docker push</c>,
    /// <c>docker login</c>, image tagging) is unchanged.
    /// </summary>
    /// <param name="builder">The Docker Compose environment resource builder.</param>
    /// <param name="endpoint">The pull registry endpoint as the remote sees it (e.g. <c>localhost:5001</c> for an unregistry sidecar on the remote, or a private mirror's hostname).</param>
    /// <returns>The resource builder for method chaining.</returns>
    /// <remarks>
    /// Implemented as an Aspire <c>ConfigureEnvFile</c> callback that runs during the prepare phase,
    /// before the <c>.env</c> file is written. Two canonical use cases:
    /// <list type="bullet">
    /// <item><description><see href="https://github.com/psviderski/unregistry">unregistry</see>: the dev box pushes through an SSH tunnel to <c>host.docker.internal:5001</c>, and the remote daemon resolves the same image as <c>localhost:5001/...</c>. Pair with <c>Deployment:PullPolicy=never</c> to skip the otherwise-redundant compose pull entirely.</description></item>
    /// <item><description>Registry mirror / image-promotion: CI pushes to a public registry, and the remote pulls from a different mirror or private registry that holds the promoted artifact.</description></item>
    /// </list>
    /// The rewrite replaces the first slash-delimited segment of each <c>*_IMAGE</c> value (which
    /// Aspire always emits as <c>{registry-endpoint}/{repo}/{name}:{tag}</c>). Values without a
    /// slash and non-image variables are left untouched.
    /// </remarks>
    [AspireExport("withPullRegistry", Description = "Rewrites generated image references so the remote Docker daemon pulls from a different registry endpoint.")]
    public static IResourceBuilder<DockerComposeEnvironmentResource> WithPullRegistry(
        this IResourceBuilder<DockerComposeEnvironmentResource> builder,
        string endpoint)
    {
        return builder.ConfigureEnvFile(envVars =>
        {
            // Snapshot keys via ToList because we may mutate values; iterating the live dict is fine,
            // but ToList future-proofs against any IDictionary impl that doesn't allow concurrent reads.
            foreach (var (name, envVar) in envVars.ToList())
            {
                var rewritten = ImageRefRewriter.RewriteIfImage(name, envVar.DefaultValue, endpoint);
                if (rewritten is not null)
                {
                    envVar.DefaultValue = rewritten;
                }
            }
        });
    }
}
