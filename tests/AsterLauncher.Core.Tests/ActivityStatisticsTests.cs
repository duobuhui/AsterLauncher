using AsterLauncher.Core;
using AsterLauncher.Infrastructure;

namespace AsterLauncher.Core.Tests;

public sealed class ActivityStatisticsTests
{
    [Fact]
    public void HeatmapSplitsMidnightAndPreservesUnmappedLegacyTotal()
    {
        var state=new GameUserState{TotalPlaySeconds=7500,PlaySessions=[new(){StartedAt=new(2026,9,28,23,30,0,TimeSpan.FromHours(8)),DurationSeconds=7200}]};
        var data=PlayActivity.Build(state,new(2026,9,28),new(2026,9,30),TimeZoneInfo.CreateCustomTimeZone("Test+8",TimeSpan.FromHours(8),"Test+8","Test+8"),new(2026,9,30,12,0,0,TimeSpan.FromHours(8)));
        Assert.Equal(new[]{1800d,5400d,0d},data.Days.Select(d=>d.Seconds));
        Assert.Equal(2,data.ActiveDays);Assert.Equal(2,data.LongestStreak);Assert.Equal(300,data.UnmappedSeconds);
    }
    [Fact]
    public void HeatmapHandlesDstAndIgnoresFutureOrInvalidSessions()
    {
        var zone=TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var state=new GameUserState{TotalPlaySeconds=9000,PlaySessions=[
            new(){StartedAt=new(2026,3,8,0,0,0,TimeSpan.FromHours(-5)),DurationSeconds=9000},
            new(){StartedAt=new(2027,1,1,0,0,0,TimeSpan.Zero),DurationSeconds=100},
            new(){StartedAt=new(2026,3,8,0,0,0,TimeSpan.Zero),DurationSeconds=double.NaN}]};
        var data=PlayActivity.Build(state,new(2026,3,8),new(2026,3,9),zone,new(2026,3,10,0,0,0,TimeSpan.Zero));
        Assert.Equal(9000,data.Seconds);Assert.Equal(1,data.Days[0].Sessions);Assert.Equal(0,data.UnmappedSeconds);
    }
    [Fact]
    public void HeatmapIncludesLeapDayWithoutAssigningOldCumulativeTime()
    {
        var state=new GameUserState{TotalPlaySeconds=3600};
        var data=PlayActivity.Build(state,new(2024,1,1),new(2024,12,31),TimeZoneInfo.Utc,new(2026,9,30,0,0,0,TimeSpan.Zero));
        Assert.Equal(366,data.Days.Count);Assert.Contains(data.Days,d=>d.Date==new DateOnly(2024,2,29));Assert.Equal(0,data.Seconds);Assert.Equal(3600,data.UnmappedSeconds);
    }
    [Fact]
    public void FeaturedIntervalsAccumulateLossesAndExcludeTruncatedFirstHistory()
    {
        var pool=Pool("11",[Pull("流萤",45,true,"3"),Pull("姬子",75,true,"2"),Pull("卡芙卡",30,false,"1")]);
        var stats=GachaStatisticsAnalyzer.Analyze(BuiltInGameIds.HonkaiStarRail,new("uid",150,[pool]),knownItems:new HashSet<string>{"流萤","卡芙卡"}).Single();
        Assert.Equal(2,stats.FeaturedPoints.Count);Assert.False(stats.FeaturedPoints[0].Complete);
        Assert.Equal(120,stats.FeaturedPoints[1].Pulls);Assert.Equal(120d,stats.FeaturedAverage);
        Assert.Equal(60d,stats.Average);Assert.Equal(90,stats.PityMaximum);Assert.Equal(180,stats.FeaturedMaximum);
    }
    [Fact]
    public void UnknownRosterBreaksFeaturedIntervalAndCorrectionsAreScoped()
    {
        var pool=Pool("11",[Pull("流萤",40,true,"4"),Pull("希儿",60,true,"3"),Pull("姬子",70,true,"2"),Pull("卡芙卡",20,false,"1")]);
        var known=new HashSet<string>{"流萤","希儿","卡芙卡"};
        var account=new UigfAccountAnalysis("uid-A",190,[pool]);
        var unknown=GachaStatisticsAnalyzer.Analyze(BuiltInGameIds.HonkaiStarRail,account,knownItems:known).Single();
        Assert.Equal(1,unknown.UnknownCount);Assert.Null(unknown.FeaturedAverage);
        var correction=new Dictionary<string,GachaUpStatus>{{GachaStatisticsAnalyzer.CorrectionKey(BuiltInGameIds.HonkaiStarRail,"uid-A","11","3"),GachaUpStatus.NonFeatured}};
        var corrected=GachaStatisticsAnalyzer.Analyze(BuiltInGameIds.HonkaiStarRail,account,correction,known).Single();
        Assert.Equal(170d,corrected.FeaturedAverage);
        Assert.Equal(1,GachaStatisticsAnalyzer.Analyze(BuiltInGameIds.HonkaiStarRail,account with{Uid="uid-B"},correction,known).Single().UnknownCount);
    }
    [Theory]
    [InlineData("姬子","2025-03-01 12:00:00",GachaUpStatus.NonFeatured)]
    [InlineData("希儿","2025-03-01 12:00:00",GachaUpStatus.Featured)]
    [InlineData("希儿","2025-04-09 12:00:00",GachaUpStatus.Unknown)]
    [InlineData("银狼","2026-04-21 12:00:00",GachaUpStatus.Featured)]
    [InlineData("银狼","2026-04-22 12:00:00",GachaUpStatus.Unknown)]
    public void StarRailRosterChangesDoNotMislabelSelectableLosses(string name,string time,GachaUpStatus expected)
    {
        var pull=Pull(name,20,true,"id") with{RawTime=time};
        Assert.Equal(expected,GachaStatisticsAnalyzer.Classify(BuiltInGameIds.HonkaiStarRail,"11",pull,new HashSet<string>{name}));
    }
    [Fact]
    public void PoolTypesNeverMixAndSpecialPoolsHaveNoInventedPityCap()
    {
        var account=new UigfAccountAnalysis("uid",100,[Pool("11",[Pull("流萤",70,true,"a")]),Pool("12",[Pull("棺的回响",30,true,"b")],80),Pool("21",[],null)]);
        var stats=GachaStatisticsAnalyzer.Analyze(BuiltInGameIds.HonkaiStarRail,account,knownItems:new HashSet<string>{"流萤","棺的回响"});
        Assert.Equal(70,stats[0].Average);Assert.Equal(30,stats[1].Average);Assert.Equal(160,stats[1].FeaturedMaximum);
        Assert.Null(stats[2].PityMaximum);Assert.Null(stats[2].FeaturedMaximum);Assert.Null(stats[2].Average);
    }
    [Fact]
    public void CurrentZenlessCustomizationNeedsExplicitCorrectionForLimitedItems()
    {
        var pull=Pull("艾莲",20,true,"id");
        Assert.Equal(GachaUpStatus.Unknown,GachaStatisticsAnalyzer.Classify(BuiltInGameIds.ZenlessZoneZero,"2",pull,new HashSet<string>{"艾莲"}));
        Assert.Equal(GachaUpStatus.NonFeatured,GachaStatisticsAnalyzer.Classify(BuiltInGameIds.ZenlessZoneZero,"2",pull with{Name="丽娜"},new HashSet<string>{"丽娜"}));
    }
    [Fact]
    public async Task CorrectionStorePersistsIndependentlyAndSupportsReset()
    {
        var root=Path.Combine(Path.GetTempPath(),"aster-statistics-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var key=GachaStatisticsAnalyzer.CorrectionKey("star-rail","uid","11","record");
            await new GachaClassificationStore(root).SetAsync(key,GachaUpStatus.NonFeatured);
            Assert.Equal(GachaUpStatus.NonFeatured,(await new GachaClassificationStore(root).ReadAsync())[key]);
            await new GachaClassificationStore(root).SetAsync(key,null);Assert.Empty(await new GachaClassificationStore(root).ReadAsync());
            await File.WriteAllTextAsync(Path.Combine(root,"gacha","up-classifications.json"),"{\"bad\":100}");
            await Assert.ThrowsAsync<InvalidDataException>(()=>new GachaClassificationStore(root).ReadAsync());
        }
        finally{Directory.Delete(root,true);}
    }
    private static UigfHighRarityPull Pull(string name,int count,bool complete,string id)=>new(BuiltInGameIds.HonkaiStarRail,name,"角色",count,complete,90,"2026/09/01",id,"banner","2026-09-01 12:00:00");
    private static UigfPoolSection Pool(string type,IReadOnlyList<UigfHighRarityPull> pulls,int? cap=90)=>new(type,type,200,pulls.Count,20,180-pulls.Count,10,cap,type=="11","五星","四星","三星","",pulls,[]);
}