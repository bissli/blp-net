namespace Blp;

/// <summary>A kind of market event an intraday request reads.</summary>
public enum IntradayEventType
{
    /// <summary>Bloomberg TRADE.</summary>
    Trade,

    /// <summary>Bloomberg BID.</summary>
    Bid,

    /// <summary>Bloomberg ASK.</summary>
    Ask,

    /// <summary>Bloomberg BID_BEST.</summary>
    BidBest,

    /// <summary>Bloomberg ASK_BEST.</summary>
    AskBest,

    /// <summary>Bloomberg MID_PRICE. Tick requests only.</summary>
    MidPrice,

    /// <summary>Bloomberg AT_TRADE. Tick requests only.</summary>
    AtTrade,

    /// <summary>Bloomberg BEST_BID.</summary>
    BestBid,

    /// <summary>Bloomberg BEST_ASK.</summary>
    BestAsk,
}
