// <copyright file="EntityFrameworkContextBase.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

using System.Threading;
using Nito.Disposables;

namespace MUnique.OpenMU.Persistence.EntityFramework;

using System.Collections;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel.Composition;
using MUnique.OpenMU.DataModel.Configuration;
using Nito.AsyncEx;
using DataGameConfiguration = MUnique.OpenMU.DataModel.Configuration.GameConfiguration;
using DataItem = MUnique.OpenMU.DataModel.Entities.Item;
using EfGameConfiguration = MUnique.OpenMU.Persistence.EntityFramework.Model.GameConfiguration;
using EfIncreasableItemOption = MUnique.OpenMU.Persistence.EntityFramework.Model.IncreasableItemOption;
using EfItem = MUnique.OpenMU.Persistence.EntityFramework.Model.Item;
using EfItemDefinition = MUnique.OpenMU.Persistence.EntityFramework.Model.ItemDefinition;
using EfItemOfItemSet = MUnique.OpenMU.Persistence.EntityFramework.Model.ItemOfItemSet;

/// <summary>
/// Abstract base class for an <see cref="IContext"/> which uses an <see cref="DbContext"/>.
/// </summary>
internal class EntityFrameworkContextBase : IContext, IItemGraphLoader
{
    private readonly bool _isOwner;
    private readonly IConfigurationChangeListener? _changeListener;
    private readonly AsyncLock _lock = new();
    private readonly ILogger _logger;
    private bool _isDisposed;
    private int _notificationSuspensions;

    /// <summary>
    /// Initializes a new instance of the <see cref="EntityFrameworkContextBase" /> class.
    /// </summary>
    /// <param name="context">The db context.</param>
    /// <param name="repositoryProvider">The repository provider.</param>
    /// <param name="isOwner">If set to <c>true</c>, this instance owns the <see cref="Context" />. That means it will be disposed when this instance will be disposed.</param>
    /// <param name="changeListener">The change listener.</param>
    /// <param name="logger">The logger.</param>
    protected EntityFrameworkContextBase(DbContext context, IContextAwareRepositoryProvider repositoryProvider, bool isOwner, IConfigurationChangeListener? changeListener, ILogger logger)
    {
        this.Context = context;
        this.RepositoryProvider = repositoryProvider;
        this._isOwner = isOwner;
        this._changeListener = changeListener;
        this._logger = logger;

        // Ensure that the model is created.
        _ = context.Model;
    }

    /// <summary>
    /// Finalizes an instance of the <see cref="EntityFrameworkContextBase"/> class.
    /// </summary>
    ~EntityFrameworkContextBase() => this.Dispose(false);

    /// <inheritdoc />
    public bool HasChanges => this.Context.ChangeTracker.HasChanges();

    /// <summary>
    /// Gets the entity framework context.
    /// </summary>
    internal DbContext Context { get; }

    /// <summary>
    /// Gets the repository provider.
    /// </summary>
    protected IContextAwareRepositoryProvider RepositoryProvider { get; }

    /// <inheritdoc/>
    public async ValueTask<bool> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        using var l = await this._lock.LockAsync();

        // when we have a change publisher attached, we want to get the changed entries before accepting them.
        // Otherwise, we can accept them.
        var acceptChanges = true;

        object? sender = null;
        SavedChangesEventArgs? args = null;
        if (this._changeListener is { })
        {
            this.Context.SavedChanges += OnSavedChanges;
            acceptChanges = false;
        }

        try
        {
            await this.Context.SaveChangesAsync(acceptChanges, cancellationToken).ConfigureAwait(false);

            if (args is not null)
            {
                await this.OnSavedChangesAsync(sender, args).ConfigureAwait(false);
            }
        }
        finally
        {
            this.Context.SavedChanges -= OnSavedChanges;
        }

        return true;

        void OnSavedChanges(object? s, SavedChangesEventArgs e)
        {
            sender = s;
            args = e;
        }
    }

    /// <inheritdoc />
    public IDisposable SuspendChangeNotifications()
    {
        Interlocked.Increment(ref this._notificationSuspensions);
        return new Disposable(() => Interlocked.Decrement(ref this._notificationSuspensions));
    }

    /// <inheritdoc />
    public bool Detach(object item)
    {
        using var l = this._lock.Lock();
        return this.DetachInternal(item);
    }

    /// <inheritdoc />
    public void Attach(object item)
    {
        using var l = this._lock.Lock();
        this.Context.Attach(item);
    }

    private bool DetachInternal(object item)
    {
        var entry = this.Context.Entry(item);
        if (entry is null)
        {
            return false;
        }

        var previousState = entry.State;
        entry.State = EntityState.Detached;
        this.ForEachAggregate(item, obj => this.DetachInternal(obj));

        return previousState != EntityState.Added;
    }

    /// <inheritdoc />
    public T CreateNew<T>(params object?[] args)
        where T : class
    {
        using var l = this._lock.Lock();
        var instance = typeof(CachingEntityFrameworkContext).Assembly.CreateNew<T>(args);
        this.Context.Add(instance);
        return instance;
    }

    /// <inheritdoc />
    public object CreateNew(Type type, params object?[] args)
    {
        using var l = this._lock.Lock();
        var instance = typeof(CachingEntityFrameworkContext).Assembly.CreateNew(type, args);
        this.Context.Add(instance);
        return instance;
    }

    /// <inheritdoc/>
    public async ValueTask<bool> DeleteAsync<T>(T obj)
        where T : class
    {
        using var l = await this._lock.LockAsync();

        var result = false;
        var entry = this.Context.Entry(obj);
        if (entry.State == EntityState.Detached)
        {
            this.Context.Attach(obj);
            entry = this.Context.Entry(obj);
        }

        switch (entry.State)
        {
            case EntityState.Detached:
                return true;
            case EntityState.Added:
                this.DetachInternal(obj);
                break;
            default:
                this.Context.Remove(obj);
                this.ForEachAggregate(obj, a => this.Context.Remove(a));
                break;
        }

        result = true;

        return result;
    }

    /// <inheritdoc/>
    public async Task<T?> GetByIdAsync<T>(Guid id, CancellationToken cancellationToken)
        where T : class
    {
        using var l = await this._lock.LockAsync(cancellationToken);
        using var context = this.RepositoryProvider.ContextStack.UseContext(this);
        return await this.GetRepository<T>().GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<object?> GetByIdAsync(Guid id, Type type, CancellationToken cancellationToken)
    {
        using var l = await this._lock.LockAsync(cancellationToken).ConfigureAwait(false);
        using var context = this.RepositoryProvider.ContextStack.UseContext(this);
        return await this.GetRepository(type).GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyDictionary<Guid, DataItem>> LoadItemGraphsByIdsAsync(
        IEnumerable<Guid> itemIds,
        DataGameConfiguration gameConfiguration,
        CancellationToken cancellationToken = default)
    {
        var ids = itemIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<Guid, DataItem>();
        }

        if (gameConfiguration is not EfGameConfiguration efGameConfiguration)
        {
            this._logger.LogWarning(
                "Batch item graph loading requires an EF game configuration, but got {GameConfigurationType}; using caller fallback.",
                gameConfiguration.GetType().FullName);
            return new Dictionary<Guid, DataItem>();
        }

        using var l = await this._lock.LockAsync(cancellationToken).ConfigureAwait(false);
        using var context = this.RepositoryProvider.ContextStack.UseContext(this);

        var items = await this.Context.Set<EfItem>()
            .Where(item => ids.Contains(item.Id))
            .Include(item => item.RawItemOptions)
            .Include(item => item.JoinedItemSetGroups)
            .AsSplitQuery()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var itemDefinitionsById = efGameConfiguration.RawItems
            .DistinctBy(definition => definition.Id)
            .ToDictionary(definition => definition.Id);
        var itemOptionsById = efGameConfiguration.RawItemOptions
            .SelectMany(optionDefinition => optionDefinition.RawPossibleOptions)
            .Concat(efGameConfiguration.RawItemSetGroups.SelectMany(group => group.RawOptions?.RawPossibleOptions ?? Enumerable.Empty<EfIncreasableItemOption>()))
            .DistinctBy(option => option.Id)
            .ToDictionary(option => option.Id);
        var itemSetItemsById = efGameConfiguration.RawItemSetGroups
            .SelectMany(group => group.RawItems)
            .DistinctBy(itemOfSet => itemOfSet.Id)
            .ToDictionary(itemOfSet => itemOfSet.Id);

        var result = new Dictionary<Guid, DataItem>(items.Count);
        foreach (var item in items)
        {
            if (TryResolveBatchItemGraph(item, itemDefinitionsById, itemOptionsById, itemSetItemsById))
            {
                MarkLoaded(item.RawItemOptions);
                MarkLoaded(item.JoinedItemSetGroups);
                result[item.Id] = item;
            }
        }

        return result;
    }

    /// <inheritdoc/>
    public async ValueTask<IEnumerable<T>> GetAsync<T>(CancellationToken cancellationToken)
        where T : class
    {
        using var l = await this._lock.LockAsync(cancellationToken).ConfigureAwait(false);
        using var context = this.RepositoryProvider.ContextStack.UseContext(this);
        return await this.GetRepository<T>().GetAllAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<IEnumerable> GetAsync(Type type, CancellationToken cancellationToken)
    {
        using var l = await this._lock.LockAsync(cancellationToken).ConfigureAwait(false);
        using var context = this.RepositoryProvider.ContextStack.UseContext(this);
        return await this.GetRepository(type).GetAllAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public bool IsSupporting(Type type)
    {
        var currentSearchType = type;
        do
        {
            if (currentSearchType is null)
            {
                break;
            }

            if (this.Context.Model.FindLeastDerivedEntityTypes(currentSearchType).FirstOrDefault() is not null)
            {
                return true;
            }

            if (currentSearchType.Name != currentSearchType.BaseType?.Name)
            {
                break;
            }

            currentSearchType = currentSearchType.BaseType;
        }
        while (currentSearchType != typeof(object));

        return false;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!this._isDisposed)
        {
            this.Dispose(true);
        }

        this._isDisposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases unmanaged and - optionally - managed resources.
    /// </summary>
    /// <param name="dispose"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
    protected virtual void Dispose(bool dispose)
    {
        if (!dispose || !this._isOwner)
        {
            return;
        }

        this.Context.Dispose();
    }

    private IRepository<T> GetRepository<T>()
        where T : class
    {
        if (this.RepositoryProvider.GetRepository<T>() is { } repository)
        {
            return repository;
        }

        throw new RepositoryNotFoundException(typeof(T));
    }

    private IRepository GetRepository(Type type)
    {
        if (this.RepositoryProvider.GetRepository(type) is { } repository)
        {
            return repository;
        }

        throw new RepositoryNotFoundException(type);
    }

    private static bool TryResolveBatchItemGraph(
        EfItem item,
        IReadOnlyDictionary<Guid, EfItemDefinition> itemDefinitionsById,
        IReadOnlyDictionary<Guid, EfIncreasableItemOption> itemOptionsById,
        IReadOnlyDictionary<Guid, EfItemOfItemSet> itemSetItemsById)
    {
        if (item.DefinitionId is not { } definitionId
            || !itemDefinitionsById.TryGetValue(definitionId, out var definition))
        {
            return false;
        }

        item.RawDefinition = definition;

        foreach (var optionLink in item.RawItemOptions)
        {
            if (optionLink.ItemOptionId is not { } optionId
                || !itemOptionsById.TryGetValue(optionId, out var option))
            {
                return false;
            }

            optionLink.RawItemOption = option;
        }

        foreach (var joinedItemSet in item.JoinedItemSetGroups)
        {
            if (!itemSetItemsById.TryGetValue(joinedItemSet.ItemOfItemSetId, out var itemOfSet))
            {
                return false;
            }

            joinedItemSet.ItemOfItemSet = itemOfSet;
        }

        return true;
    }

    private static void MarkLoaded(object collection)
    {
        if (collection is ILoadingStatusAware loadingStatusAware)
        {
            loadingStatusAware.LoadingStatus = LoadingStatus.Loaded;
        }
    }

    private void ForEachAggregate(object obj, Action<object> action)
    {
        var aggregateProperties = obj.GetType()
            .GetProperties(BindingFlags.FlattenHierarchy | BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<MemberOfAggregateAttribute>() is { }
                        || p.Name.StartsWith("Joined"));
        foreach (var propertyInfo in aggregateProperties)
        {
            var propertyValue = propertyInfo.GetMethod?.Invoke(obj, []);
            if (propertyValue is IEnumerable enumerable)
            {
                foreach (var value in enumerable)
                {
                    action(value);
                }
            }
            else if (propertyValue is { })
            {
                action(propertyValue);
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = "Catching all Exceptions.")]
    private async ValueTask OnSavedChangesAsync(object? sender, SavedChangesEventArgs e)
    {
        try
        {
            if (this._changeListener is null || this._notificationSuspensions > 0)
            {
                // should never be the case
                return;
            }

            if (e.EntitiesSavedCount == 0)
            {
                // why are we getting this event then anyway?
                return;
            }

            var changedEntries = this.Context.ChangeTracker.Entries()
                .Where(entity => entity.State != EntityState.Unchanged).ToList();
            foreach (var entry in changedEntries)
            {
                var (parent, parentCollectionNavigation) = this.GetParentInformation(entry);

                switch (entry.State)
                {
                    case EntityState.Added:
                        await this._changeListener.ConfigurationAddedAsync(entry.Metadata.ClrType, entry.Entity.GetId(), entry.Entity, parent, parentCollectionNavigation).ConfigureAwait(false);
                        break;
                    case EntityState.Deleted:
                        await this._changeListener.ConfigurationRemovedAsync(entry.Metadata.ClrType, entry.Entity.GetId(), parent, parentCollectionNavigation).ConfigureAwait(false);
                        break;
                    case EntityState.Modified:
                        await this._changeListener.ConfigurationChangedAsync(entry.Metadata.ClrType, entry.Entity.GetId(), entry.Entity, parent).ConfigureAwait(false);
                        break;
                    default:
                        // no change publishing required.
                        break;
                }

                if (parent is not null && parent is not GameConfiguration && parent is not Guid)
                {
                    await this._changeListener.ConfigurationChangedAsync(parent.GetType(), parent.GetId(), parent, null).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Unexpected error publishing changes.");
        }
        finally
        {
            try
            {
                this.Context.ChangeTracker.AcceptAllChanges();
            }
            catch (Exception ex)
            {
                this._logger.LogError(ex, "Unexpected error when accepting all saved changes.");
            }
        }
    }

    private (object? Parent, INavigationBase? ParentCollectionNavigation) GetParentInformation(EntityEntry entry)
    {
        var propertyToParent = entry.Properties
            .FirstOrDefault(p => p.Metadata.IsForeignKey()
                                 && (p.Metadata.IsShadowProperty() || p.Metadata.PropertyInfo?.GetCustomAttribute<IsLinkToParentAttribute>() is not null));

        var parentId = (Guid?)(propertyToParent?.CurrentValue ?? propertyToParent?.OriginalValue);
        if (parentId is null)
        {
            return (null, null);
        }

        object? parent = null;
        INavigationBase? parentCollectionNavigation = null;
        var parentEntry = this.Context.ChangeTracker.Entries().FirstOrDefault(e => e.Entity.GetId() == parentId);
        if (parentEntry is not null && propertyToParent is not null)
        {
            parent = parentEntry.Entity;
            var parentCollection = parentEntry.Collections
                .FirstOrDefault(c => c.Metadata.IsCollection
                                     && (c.Metadata as INavigation)?.ForeignKey == propertyToParent.Metadata.GetContainingForeignKeys().FirstOrDefault());
            parentCollectionNavigation = parentCollection?.Metadata;
        }

        return (parent ?? parentId, parentCollectionNavigation);
    }
}
