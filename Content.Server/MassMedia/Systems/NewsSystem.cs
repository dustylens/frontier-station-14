using System.Linq;
using Content.Server.Administration.Logs;
using Content.Server.CartridgeLoader;
using Content.Server.CartridgeLoader.Cartridges;
using Content.Server.GameTicking;
using System.Diagnostics.CodeAnalysis;
using Content.Server.Access.Systems;
using Content.Server.Popups;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.CartridgeLoader;
using Content.Shared.CartridgeLoader.Cartridges;
using Content.Shared.Database;
using Content.Shared.MassMedia.Components;
using Content.Shared.MassMedia.Systems;
using Robust.Server.GameObjects;
using Content.Server.MassMedia.Components;
using Robust.Shared.Timing;
using Content.Server.Station.Systems;
using Content.Shared.Popups;
using Content.Shared.StationRecords;
using Robust.Shared.Audio.Systems;
using Content.Server.Chat.Managers;
using Content.Shared.GameTicking; // Frontier

namespace Content.Server.MassMedia.Systems;

public sealed class NewsSystem : SharedNewsSystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IAdminLogManager _adminLogger = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly CartridgeLoaderSystem _cartridgeLoaderSystem = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly GameTicker _ticker = default!;
    [Dependency] private readonly AccessReaderSystem _accessReader = default!;
    [Dependency] private readonly IdCardSystem _idCardSystem = default!;
    [Dependency] private readonly IChatManager _chatManager = default!;

    public override void Initialize()
    {
        base.Initialize();

        // News writer
        // Frontier: News is shared across the sector.  No need to create shuttle-local news caches.
        // SubscribeLocalEvent<NewsWriterComponent, MapInitEvent>(OnMapInit);

        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
        // End Frontier

        // New writer bui messages
        Subs.BuiEvents<NewsWriterComponent>(NewsWriterUiKey.Key, subs =>
        {
            subs.Event<NewsWriterDeleteMessage>(OnWriteUiDeleteMessage);
            subs.Event<NewsWriterArticlesRequestMessage>(OnRequestArticlesUiMessage);
            subs.Event<NewsWriterPublishMessage>(OnWriteUiPublishMessage);
        });

        // News reader
        SubscribeLocalEvent<NewsReaderCartridgeComponent, NewsArticlePublishedEvent>(OnArticlePublished);
        SubscribeLocalEvent<NewsReaderCartridgeComponent, NewsArticleDeletedEvent>(OnArticleDeleted);
        SubscribeLocalEvent<NewsReaderCartridgeComponent, CartridgeMessageEvent>(OnReaderUiMessage);
        SubscribeLocalEvent<NewsReaderCartridgeComponent, CartridgeUiReadyEvent>(OnReaderUiReady);
    }
 
    // Frontier: article lifecycle management
    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        // A new round is starting, clear any articles from the previous round.
        SectorNewsComponent.Articles.Clear();
    }
    // End Frontier

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<NewsWriterComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (comp.PublishEnabled || _timing.CurTime < comp.NextPublish)
                continue;

            comp.PublishEnabled = true;
            UpdateWriterUi((uid, comp));
        }
    }

    #region Writer Event Handlers

    // Frontier: News is shared across the sector.  No need to create shuttle-local news caches.
    // private void OnMapInit(Entity<NewsWriterComponent> ent, ref MapInitEvent args)
    // {
    //     var station = _station.GetOwningStation(ent);
    //     if (!station.HasValue) {
    //         return;
    //     }

    //     EnsureComp<StationNewsComponent>(station.Value);
    // }
    // End Frontier

    private void OnWriteUiDeleteMessage(Entity<NewsWriterComponent> ent, ref NewsWriterDeleteMessage msg)
    {
        if (!TryGetArticles(ent, out var articles))
            return;

        if (msg.ArticleNum >= articles.Count)
            return;

        var article = articles[msg.ArticleNum];
        if (CheckDeleteAccess(article, ent, msg.Actor))
        {
            _adminLogger.Add(
                LogType.Chat, LogImpact.Medium,
                $"{ToPrettyString(msg.Actor):actor} deleted news article {article.Title} by {article.Author}: {article.Content}"
                );

            articles.RemoveAt(msg.ArticleNum);
            _audio.PlayPvs(ent.Comp.ConfirmSound, ent);
        }
        else
        {
            _popup.PopupEntity(Loc.GetString("news-write-no-access-popup"), ent, PopupType.SmallCaution);
            _audio.PlayPvs(ent.Comp.NoAccessSound, ent);
        }

        var args = new NewsArticleDeletedEvent();
        var query = EntityQueryEnumerator<NewsReaderCartridgeComponent>();
        while (query.MoveNext(out var readerUid, out _))
        {
            RaiseLocalEvent(readerUid, ref args);
        }

        UpdateWriterDevices();
    }

    private void OnRequestArticlesUiMessage(Entity<NewsWriterComponent> ent, ref NewsWriterArticlesRequestMessage msg)
    {
        UpdateWriterUi(ent);
    }

    private void OnWriteUiPublishMessage(Entity<NewsWriterComponent> ent, ref NewsWriterPublishMessage msg)
    {
        if (!ent.Comp.PublishEnabled)
            return;

        ent.Comp.PublishEnabled = false;
        ent.Comp.NextPublish = _timing.CurTime + TimeSpan.FromSeconds(ent.Comp.PublishCooldown);

        if (!TryGetArticles(ent, out var articles))
        {
            Log.Error("OnWriteUiPublishMessage: no articles!");
            return;
        }

        if (!_accessReader.FindStationRecordKeys(msg.Actor, out _))
        {
            Log.Error("OnWriteUiPublishMessage: FindStationRecordKeys failed!");
            return;
        }

        string? authorName = null;
        if (_idCardSystem.TryFindIdCard(msg.Actor, out var idCard))
            authorName = idCard.Comp.FullName;

        var title = msg.Title.Trim();
        var content = msg.Content.Trim();

        var article = new NewsArticle
        {
            Title = title.Length <= MaxTitleLength ? title : $"{title[..MaxTitleLength]}...",
            Content = content.Length <= MaxContentLength ? content : $"{content[..MaxContentLength]}...",
            Author = authorName,
            ShareTime = _ticker.RoundDuration()
        };

        _audio.PlayPvs(ent.Comp.ConfirmSound, ent);

        _adminLogger.Add(
            LogType.Chat,
            LogImpact.Medium,
            $"{ToPrettyString(msg.Actor):actor} created news article {article.Title} by {article.Author}: {article.Content}"
            );

        _chatManager.SendAdminAnnouncement(Loc.GetString("news-publish-admin-announcement",
            ("actor", msg.Actor),
            ("title", article.Title),
            ("author", article.Author ?? Loc.GetString("news-read-ui-no-author"))
            ));

        articles.Add(article);

        var args = new NewsArticlePublishedEvent(article);
        var query = EntityQueryEnumerator<NewsReaderCartridgeComponent>();
        while (query.MoveNext(out var readerUid, out _))
        {
            RaiseLocalEvent(readerUid, ref args);
        }

        UpdateWriterDevices();
    }
    #endregion

    #region Reader Event Handlers

    private void OnArticlePublished(Entity<NewsReaderCartridgeComponent> ent, ref NewsArticlePublishedEvent args)
    {
        if (Comp<CartridgeComponent>(ent).LoaderUid is not { } loaderUid)
            return;

        UpdateReaderUi(ent, loaderUid);

        if (!ent.Comp.NotificationOn)
            return;

        _cartridgeLoaderSystem.SendNotification(
            loaderUid,
            Loc.GetString("news-pda-notification-header"),
            args.Article.Title);
    }

    private void OnArticleDeleted(Entity<NewsReaderCartridgeComponent> ent, ref NewsArticleDeletedEvent args)
    {
        if (Comp<CartridgeComponent>(ent).LoaderUid is not { } loaderUid)
            return;

        UpdateReaderUi(ent, loaderUid);
    }

    private void OnReaderUiMessage(Entity<NewsReaderCartridgeComponent> ent, ref CartridgeMessageEvent args)
    {
        if (args is not NewsReaderUiMessageEvent message)
            return;

        switch (message.Action)
        {
            case NewsReaderUiAction.Next:
                NewsReaderLeafArticle(ent, 1);
                break;
            case NewsReaderUiAction.Prev:
                NewsReaderLeafArticle(ent, -1);
                break;
            case NewsReaderUiAction.NotificationSwitch:
                ent.Comp.NotificationOn = !ent.Comp.NotificationOn;
                break;
        }

        UpdateReaderUi(ent, GetEntity(args.LoaderUid));
    }

    private void OnReaderUiReady(Entity<NewsReaderCartridgeComponent> ent, ref CartridgeUiReadyEvent args)
    {
        UpdateReaderUi(ent, args.Loader);
    }
    #endregion

    private bool TryGetArticles(EntityUid uid, [NotNullWhen(true)] out List<NewsArticle>? articles)
    {
        // Frontier: Get sector-wide article set instead of set for this station.
        // if (_station.GetOwningStation(uid) is not { } station ||
        //     !TryComp<StationNewsComponent>(station, out var stationNews))
        // {
        //     articles = null;
        //     return false;
        // }
        // articles = stationNews.Articles;
        // return true;

        // Any SectorNewsComponent will have a complete article set, we ensure one exists before returning the complete set.
        var query = EntityQueryEnumerator<SectorNewsComponent>();
        if (query.MoveNext(out var _)) {
            articles = SectorNewsComponent.Articles;
            return true;
        }
        articles = null;
        return false;
        // End Frontier
    }

    private void UpdateWriterUi(Entity<NewsWriterComponent> ent)
    {
        if (!_ui.HasUi(ent, NewsWriterUiKey.Key))
            return;

        if (!TryGetArticles(ent, out var articles))
            return;

        var state = new NewsWriterBoundUserInterfaceState(articles.ToArray(), ent.Comp.PublishEnabled, ent.Comp.NextPublish);
        _ui.SetUiState(ent.Owner, NewsWriterUiKey.Key, state);
    }

    private void UpdateReaderUi(Entity<NewsReaderCartridgeComponent> ent, EntityUid loaderUid)
    {
        if (!TryGetArticles(ent, out var articles))
            return;

        NewsReaderLeafArticle(ent, 0);

        if (articles.Count == 0)
        {
            _cartridgeLoaderSystem.UpdateCartridgeUiState(loaderUid, new NewsReaderEmptyBoundUserInterfaceState(ent.Comp.NotificationOn));
            return;
        }

        var state = new NewsReaderBoundUserInterfaceState(
            articles[ent.Comp.ArticleNumber],
            ent.Comp.ArticleNumber + 1,
            articles.Count,
            ent.Comp.NotificationOn);

        _cartridgeLoaderSystem.UpdateCartridgeUiState(loaderUid, state);
    }

    private void NewsReaderLeafArticle(Entity<NewsReaderCartridgeComponent> ent, int leafDir)
    {
        if (!TryGetArticles(ent, out var articles))
            return;

        ent.Comp.ArticleNumber += leafDir;

        if (ent.Comp.ArticleNumber >= articles.Count)
            ent.Comp.ArticleNumber = 0;

        if (ent.Comp.ArticleNumber < 0)
            ent.Comp.ArticleNumber = articles.Count - 1;
    }

    private void UpdateWriterDevices()
    {
        var query = EntityQueryEnumerator<NewsWriterComponent>();
        while (query.MoveNext(out var owner, out var comp))
        {
            UpdateWriterUi((owner, comp));
        }
    }

    private bool CheckDeleteAccess(NewsArticle articleToDelete, EntityUid device, EntityUid user)
    {
        if (TryComp<AccessReaderComponent>(device, out var accessReader) &&
            _accessReader.IsAllowed(user, device, accessReader))
            return true;

        if (articleToDelete.AuthorStationRecordKeyIds == null || articleToDelete.AuthorStationRecordKeyIds.Count == 0)
            return true;

        return _accessReader.FindStationRecordKeys(user, out var recordKeys)
               && StationRecordsToNetEntities(recordKeys).Intersect(articleToDelete.AuthorStationRecordKeyIds).Any();
    }

    private ICollection<(NetEntity, uint)> StationRecordsToNetEntities(IEnumerable<StationRecordKey> records)
    {
        return records.Select(record => (GetNetEntity(record.OriginStation), record.Id)).ToList();
    }
}
