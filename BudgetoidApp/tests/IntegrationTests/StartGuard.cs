namespace IntegrationTests;

/// <summary>
/// Starts a resource that is disposable before it is startable, and disposes it if the start throws.
/// </summary>
/// <remarks>
/// <para>
/// <b>The failure it closes.</b> Every call site has the shape
/// <c>await using PostgreSqlContainer container = await StartBareContainerAsync();</c>, so the
/// variable is bound only <b>after</b> the helper returns. When the start throws, nothing is ever
/// disposed — and Docker has created and started the container long before a readiness check gives up.
/// That was checked directly against Testcontainers 4.12.0: the container is <c>Up</c> at the moment
/// <c>StartAsync</c> raises its <see cref="TimeoutException" /> and stays that way until
/// <c>DisposeAsync</c> stops it. An abandoned one keeps its memory and its port binding for the rest
/// of the run, which makes the next start likelier to time out in turn — the feedback loop
/// <c>AssemblyInfo.cs</c> describes, and the reason a concurrency cap once looked like the fix.
/// </para>
/// <para>
/// <b>Why it is a helper rather than a try/catch written twice.</b> Two classes deliberately keep
/// containers of their own — <see cref="DeploymentProvisioningTests" /> and
/// <see cref="NonSuperuserDeploymentProvisioningTests" />, which assert on the creation of
/// cluster-level roles that must not already exist and so cannot join the shared cluster. Both carried
/// the identical guard, argued in a paragraph on one of them and cross-referenced from the other. A
/// guard written twice is a guard that can be corrected once, and — the thing that actually motivated
/// the move — a <c>catch</c> reachable only by a Docker daemon failing is a <c>catch</c> nothing can
/// execute. Behind an interface the caller supplies, it is reachable from a fake, and
/// <see cref="StartGuardTests" /> is what reaches it. The guard landed on a shape argument and a
/// leaked <c>postgres:17</c> container found on a developer machine, with no reproduced failure; this
/// is the smallest change that turns it from an argument into a claim something checks.
/// </para>
/// <para>
/// <b><c>SharedPostgresCluster.StartClusterAsync</c> deliberately does not use this.</b> Its
/// <c>try</c> spans the start <em>and</em> the template database it builds afterwards, so the region
/// it guards is not "the start" and folding it in here would mean generalising this helper into
/// something that takes a whole body and returns something other than the resource. That is a wider
/// abstraction bought for one call site, and the two are not the same shape however similar they look.
/// </para>
/// </remarks>
internal static class StartGuard
{
    /// <summary>
    /// Runs <paramref name="startAsync" /> against <paramref name="resource" /> and hands the resource
    /// back, disposing it and rethrowing if the start fails.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The start is a delegate rather than an interface constraint</b>, because
    /// <see cref="Testcontainers.PostgreSql.PostgreSqlContainer" />'s <c>StartAsync</c> comes from
    /// <c>IContainer</c> and a fake implementing that would have to model a container. What this
    /// method needs is exactly two capabilities — something that can be started and can be disposed —
    /// and the delegate supplies the first without a test having to impersonate Docker to reach the
    /// second.
    /// </para>
    /// <para>
    /// <b>A disposal that throws does not replace the start failure.</b> Left unguarded,
    /// <c>await resource.DisposeAsync()</c> inside the <c>catch</c> discards the exception being
    /// handled, so a Docker daemon that has gone away reads as a disposal error and sends the next
    /// person to the wrong place — the exact misdiagnosis this guard exists to make less likely, and
    /// the container leaks anyway. The tempting shorter fix is to swallow the disposal failure and
    /// rethrow the original; that is wrong in the other direction, because a resource that refused to
    /// be disposed is still out there and nothing would say so. Both travel, in an
    /// <see cref="AggregateException" /> whose first inner exception is the start failure. Nothing
    /// here catches by type, so the change of type costs a caller nothing.
    /// </para>
    /// </remarks>
    /// <typeparam name="TResource">
    /// The resource being started. Constrained to <see langword="class" /> as well as
    /// <see cref="IAsyncDisposable" />: a value type would be boxed by the null check and then disposed
    /// through a copy, which is a silently wrong thing for a guard to do.
    /// </typeparam>
    internal static async Task<TResource> StartAsync<TResource>(
        TResource resource,
        Func<TResource, Task> startAsync)
        where TResource : class, IAsyncDisposable
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(startAsync);

        try
        {
            await startAsync(resource);
            return resource;
        }
        catch (Exception startFailure)
        {
            try
            {
                // The caller never receives the resource on this path, so nothing else can close it.
                await resource.DisposeAsync();
            }
            catch (Exception disposalFailure)
            {
                throw new AggregateException(startFailure, disposalFailure);
            }

            throw;
        }
    }
}
