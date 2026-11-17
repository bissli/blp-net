import _thread
import datetime
import math
import threading
from typing import Any

import numpy as np
import pandas as pd
import pytest
import System
from Blp import FieldError as NetFieldError
from Blp import IntradayEventType, RequestFieldException
from Blp import RequestSecurityException, RequestTimeoutException
from Blp import ResponseException
from Blp import SecurityError as NetSecurityError
from Blp import SessionException
from Blp.Bql import BqlResponse
from Blp.Eqs import EqsResponse, EqsScreenType
from Blp.HistoricalData import HistoricalDataResponse, HistoricalDataRow
from Blp.HistoricalData import NonTradingDayFillMethod
from Blp.HistoricalData import NonTradingDayFillOption, OverrideOption
from Blp.HistoricalData import Periodicity, PeriodicityAdjustment
from Blp.HistoricalData import PricingOption
from Blp.ReferenceData import ReferenceDataResponse
from opendate import Date, DateTime, Time, Timezone
from System import DateOnly, DateTimeOffset, Object, String, TimeOnly, TimeSpan
from System.Collections.Generic import Dictionary, IReadOnlyDictionary
from System.Collections.Generic import IReadOnlyList, List
from System.Threading.Tasks import Task, TaskCompletionSource

import blp.client
from blp.client import Blp, FieldError, RequestFieldError
from blp.client import RequestSecurityError, RequestTimeoutError
from blp.client import ResponseError, SecurityError, SessionError
from blp.client import dispose_live_clients, live_clients, to_python

TOKYO = Timezone('Asia/Tokyo')


class StubClient:
    """Stand-in BlpClient: records each request and token, and answers
    with the task answer builds.
    """

    def __init__(self, answer: Any = None) -> None:
        self.answer = answer
        self.requests: list = []
        self.tokens: list = []
        self.dispose_cnt = 0

    def respond(self, request: Any, token: Any) -> Any:
        self.requests.append(request)
        self.tokens.append(token)
        if isinstance(self.answer, System.Exception):
            return Task.FromException[Object](self.answer)
        if self.answer is None:
            return TaskCompletionSource[Object]().Task
        return Task.FromResult[Object](self.answer)

    GetReferenceDataAsync = respond
    GetHistoricalDataAsync = respond
    GetIntradayTicksAsync = respond
    GetIntradayBarsAsync = respond
    GetScreenerAsync = respond
    GetBqlAsync = respond

    def Dispose(self) -> None:
        self.dispose_cnt += 1


class StubConnector:
    """Stand-in for the BlpClient class: ConnectAsync records its options
    and returns client.
    """

    def __init__(self, client: StubClient) -> None:
        self.client = client
        self.options: list = []

    def ConnectAsync(self, options: Any, token: Any) -> Any:
        self.options.append(options)
        return Task.FromResult[Object](self.client)


@pytest.fixture(autouse=True)
def clear_live_clients():
    yield
    live_clients.clear()


def connect(monkeypatch: pytest.MonkeyPatch, client: StubClient, **kwargs: Any) -> Blp:
    """Blp whose connect returns client.
    """
    monkeypatch.setattr(blp.client, 'BlpClient', StubConnector(client))
    return Blp(**kwargs)


def net_row(**values: Any) -> Dictionary[String, Object]:
    """.NET name-to-value row, in keyword order.
    """
    row = Dictionary[String, Object]()
    for name, value in values.items():
        row[name] = value
    return row


def reference_response(
    rows: dict[str, Any],
    security_errors: list | None = None,
    field_errors: list | None = None) -> ReferenceDataResponse:
    """blp-net ReferenceDataResponse holding rows, in dict order.
    """
    values = Dictionary[String, IReadOnlyDictionary[String, Object]]()
    for ticker, row in rows.items():
        values[ticker] = row
    net_security_errors = List[NetSecurityError]()
    for error in security_errors or []:
        net_security_errors.Add(error)
    net_field_errors = List[NetFieldError]()
    for error in field_errors or []:
        net_field_errors.Add(error)
    return ReferenceDataResponse(values, net_security_errors, net_field_errors)


def historical_response(points: dict[str, list]) -> HistoricalDataResponse:
    """blp-net HistoricalDataResponse with each ticker's (DateOnly, row)
    points, in dict order.
    """
    rows = Dictionary[String, IReadOnlyList[HistoricalDataRow]]()
    for ticker, ticker_points in points.items():
        point_list = List[HistoricalDataRow]()
        for date, row in ticker_points:
            point_list.Add(HistoricalDataRow(date, row))
        rows[ticker] = point_list
    return HistoricalDataResponse(rows, List[NetSecurityError](), List[NetFieldError]())


def test_reference_data_passes_string_options(monkeypatch):
    """Verify get_reference_data sets each named keyword and the zone on
    the request, and sends only the other keywords as overrides, each as
    its str.

    Mutation: a named keyword not passed on, the two raise flags swapped,
        or an override value sent as other than its str.
    Oracle: the request a recording stub client receives, with one raise
        flag set and the other left off.
    """
    client = StubClient(reference_response({}))
    connect(monkeypatch, client).get_reference_data(
        'IBM US Equity',
        'PX_LAST',
        'Asia/Tokyo',
        raise_security_error=True,
        force_string=True,
        time_as_datetime=True,
        EQY_FUND_CRNCY='USD',
        SETTLE_DT=20240102)
    request = client.requests[0]
    assert list(request.Tickers) == ['IBM US Equity']
    assert list(request.Fields) == ['PX_LAST']
    assert request.RaiseSecurityError
    assert not request.RaiseFieldError
    assert request.ForceString
    assert request.TimeAsDateTime
    assert request.TimeZone.Id == 'Asia/Tokyo'
    assert {pair.Key: pair.Value for pair in request.Overrides} == {
        'EQY_FUND_CRNCY': 'USD',
        'SETTLE_DT': '20240102',
        }


def test_limits_are_read_at_each_request(monkeypatch):
    """Verify each request takes idle_seconds and total_seconds as they
    stand when it is sent.

    Mutation: the limits fixed at construction, so a caller that sets
        idle_seconds before a request keeps the old limit.
    Oracle: the router agent sets both on a shared Blp before each request.
    """
    client = StubClient(reference_response({}))
    connector = StubConnector(client)
    monkeypatch.setattr(blp.client, 'BlpClient', connector)
    session = Blp(idle_seconds=8)
    session.idle_seconds = 3
    session.total_seconds = 20
    session.get_reference_data('IBM US Equity', 'PX_LAST')
    assert connector.options[0].IdleTimeout == TimeSpan.FromSeconds(8)
    assert connector.options[0].TotalTimeout is None
    assert client.requests[0].IdleTimeout == TimeSpan.FromSeconds(3)
    assert client.requests[0].TotalTimeout == TimeSpan.FromSeconds(20)


@pytest.mark.parametrize(('method', 'keyword'), [
    ('get_reference_data', 'decimal_places'),
    ('get_reference_data', 'return_formatted_value'),
    ('get_historical', 'force_string'),
    ])
def test_gap_keyword_raises(monkeypatch, method, keyword):
    """Verify a Python blp keyword that blp-net lacks raises TypeError and
    sends nothing.

    Mutation: the gap keyword left in **overrides, which sends it to
        Bloomberg as a field override.
    Oracle: the stub client records no request.
    """
    client = StubClient(reference_response({}))
    session = connect(monkeypatch, client)
    with pytest.raises(TypeError, match=keyword):
        getattr(session, method)('IBM US Equity', 'PX_LAST', **{keyword: True})
    assert client.requests == []


@pytest.mark.parametrize('value', [
    '2024-01-02T20:00:00-05:00',
    '2024-01-02T08:00+09:00',
    Date(2024, 1, 2),
    DateTime(2024, 1, 2, 23, 0),
    ])
def test_historical_date_in_own_timezone(monkeypatch, value):
    """Verify each input sends the calendar date it names in its own zone.

    Mutation: the date taken after conversion to UTC, which sends
        2024-01-03 for the -05:00 input and 2024-01-01 for the +09:00 input.
    Oracle: every input reads January 2 where it was written.
    """
    client = StubClient(historical_response({}))
    connect(monkeypatch, client).get_historical('IBM US Equity', 'PX_LAST', value, value)
    assert client.requests[0].Start == DateOnly(2024, 1, 2)
    assert client.requests[0].End == DateOnly(2024, 1, 2)


def test_historical_unreadable_date_raises(monkeypatch):
    """Verify a string with no date in it raises before any request.

    Mutation: Date.parse's None kept as the date, which fails later with
        an AttributeError.
    Oracle: 'garbage', a string Date.parse returns None for.
    """
    session = connect(monkeypatch, StubClient(historical_response({})))
    with pytest.raises(ValueError, match="'garbage'"):
        session.get_historical('IBM US Equity', 'PX_LAST', start='garbage', end='2024-01-02')


@pytest.mark.parametrize('missing', [None, np.nan, pd.NaT])
def test_historical_null_date_takes_the_default(monkeypatch, missing):
    """Verify a None, NaN or NaT start or end leaves the date to its
    default.

    Mutation: only None counted as missing, so NaN raises TypeError and
        NaT raises AttributeError.
    Oracle: the Python blp's is_null test, which counts all three.
    """
    client = StubClient(historical_response({}))
    connect(monkeypatch, client).get_historical('IBM US Equity', 'PX_LAST', missing, missing)
    assert client.requests[0].Start is None
    assert client.requests[0].End is None


def test_unknown_zone_raises_before_the_request(monkeypatch):
    """Verify a zone opendate cannot read raises before any request.

    Mutation: the opendate Timezone built after the response, which spends
        a Bloomberg request before the error.
    Oracle: 'Eastern Standard Time', a Windows zone id .NET reads and
        opendate does not.
    """
    client = StubClient(reference_response({}))
    session = connect(monkeypatch, client)
    with pytest.raises(ValueError):
        session.get_reference_data('IBM US Equity', 'PX_LAST', 'Eastern Standard Time')
    assert client.requests == []


def test_historical_options_follow_python_rules(monkeypatch):
    """Verify Bloomberg option names map to their members, each option
    reaches its own property, and the Python blp's falsy rules hold.

    Mutation: an OVERRIDE_OPTION_ or PRICING_OPTION_ prefix left on the
        lookup, NEXTDAY_VALUE mapped to NextDayValue, two adjustment flags
        or the two raise flags crossed, an explicit False flag dropped, or
        an empty currency or a zero cap sent.
    Oracle: the Python blp's create_request, which skips a falsy option
        and sends a False flag; each flag pair is set to differing values.
    """
    client = StubClient(historical_response({}))
    connect(monkeypatch, client).get_historical(
        'IBM US Equity',
        'PX_LAST',
        period='SEMI_ANNUALLY',
        raise_field_error=True,
        period_adjustment='FISCAL',
        override_option='OVERRIDE_OPTION_GPA',
        pricing_option='PRICING_OPTION_YIELD',
        non_trading_day_fill_option='ALL_CALENDAR_DAYS',
        non_trading_day_fill_method='NEXTDAY_VALUE',
        adjustment_normal=True,
        adjustment_split=False,
        adjustment_follow_DPDF=True,
        calendar_code_override='US',
        currency='',
        max_data_points=0)
    request = client.requests[0]
    assert request.Period == Periodicity.SemiAnnually
    assert not request.RaiseSecurityError
    assert request.RaiseFieldError
    assert request.PeriodAdjustment == PeriodicityAdjustment.Fiscal
    assert request.OverrideOption == OverrideOption.Gpa
    assert request.PricingOption == PricingOption.Yield
    assert request.NonTradingDayFillOption == NonTradingDayFillOption.AllCalendarDays
    assert request.NonTradingDayFillMethod == NonTradingDayFillMethod.NextdayValue
    assert request.AdjustmentNormal is True
    assert request.AdjustmentAbnormal is None
    assert request.AdjustmentSplit is False
    assert request.AdjustmentFollowDpdf is True
    assert request.CalendarCodeOverride == 'US'
    assert request.Currency is None
    assert request.MaxDataPoints is None


def test_unknown_option_name_raises(monkeypatch):
    """Verify an option name with no member raises before any request.

    Mutation: the name passed through to an attribute lookup that raises
        AttributeError, or to a default member.
    Oracle: 'BIWEEKLY', which Bloomberg's periodicity list lacks.
    """
    client = StubClient(historical_response({}))
    session = connect(monkeypatch, client)
    with pytest.raises(ValueError, match='BIWEEKLY'):
        session.get_historical('IBM US Equity', 'PX_LAST', period='BIWEEKLY')
    assert client.requests == []


@pytest.mark.parametrize(('value', 'expected'), [
    (DateOnly(2024, 1, 2), Date(2024, 1, 2)),
    (
        DateTimeOffset(2024, 1, 2, 9, 30, 0, 123, TimeSpan.FromHours(-5)),
        DateTime(2024, 1, 2, 23, 30, 0, 123000, tzinfo=TOKYO),
        ),
    (TimeOnly(9, 30, 1), Time(9, 30, 1, tzinfo=TOKYO)),
    ])
def test_value_converts_to_python(value, expected):
    """Verify each blp-net value type becomes its opendate or Python value.

    Mutation: the DateTimeOffset's wall clock read as UTC (18:30 in Tokyo),
        its milliseconds dropped, or the Time left without the zone.
    Oracle: 09:30:00.123 at -05:00 is 14:30:00.123 UTC, 23:30:00.123 in
        Tokyo.
    """
    converted = to_python(value, TOKYO)
    assert converted == expected
    assert getattr(converted, 'tzinfo', None) == getattr(expected, 'tzinfo', None)


def test_null_value_is_nan():
    """Verify a null value becomes NaN.

    Mutation: null kept as None, which turns a float column to object.
    Oracle: the Python blp's np.nan for a null number or absent field.
    """
    assert math.isnan(to_python(None, TOKYO))


def test_bulk_value_is_dataframe():
    """Verify bulk rows become a DataFrame with each cell converted and
    NaN for a null cell.

    Mutation: a nested DateOnly left as a .NET object, or a null cell kept
        as None in an object column.
    Oracle: two hand-built rows, the second with null cells.
    """
    rows = List[IReadOnlyDictionary[String, Object]]()
    rows.Add(net_row(Member='AAPL', Weight=0.5, Added=DateOnly(2024, 1, 2)))
    rows.Add(net_row(Member='MSFT', Weight=None, Added=None))
    df = to_python(rows, TOKYO)
    assert list(df.columns) == ['Member', 'Weight', 'Added']
    assert df['Member'].tolist() == ['AAPL', 'MSFT']
    assert df['Added'].iloc[0] == Date(2024, 1, 2)
    assert math.isnan(df['Added'].iloc[1])
    assert df['Weight'].iloc[0] == 0.5
    assert math.isnan(df['Weight'].iloc[1])


def test_reference_data_frame_shape(monkeypatch):
    """Verify as_dict and as_dataframe keep securities in arrival order and
    fields in request order.

    Mutation: field order taken from the .NET row, which here holds NAME
        first, or securities sorted.
    Oracle: rows built with NAME before PX_LAST, ZZZ before AAA.
    """
    response = reference_response({
        'ZZZ US Equity': net_row(NAME='ZZZ', PX_LAST=2.5),
        'AAA US Equity': net_row(NAME='AAA', PX_LAST=None),
        })
    result = connect(monkeypatch, StubClient(response)).get_reference_data(
        ['ZZZ US Equity', 'AAA US Equity'],
        ['PX_LAST', 'NAME'])
    assert list(result.as_dict()) == ['ZZZ US Equity', 'AAA US Equity']
    assert list(result.as_dict()['ZZZ US Equity']) == ['PX_LAST', 'NAME']
    df = result.as_dataframe()
    assert list(df.index) == ['ZZZ US Equity', 'AAA US Equity']
    assert list(df.columns) == ['PX_LAST', 'NAME']
    assert df['PX_LAST'].dtype == 'float64'


def test_reference_data_errors_are_tuples(monkeypatch):
    """Verify response.request lists each error as the Python blp's tuple,
    where a Python blp caller reads it.

    Mutation: two attributes swapped, such as category and subcategory, or
        the lists moved off response.request.
    Oracle: hand-built error records with distinct values in every slot.
    """
    response = reference_response(
        {},
        [NetSecurityError('BAD US Equity', 'BAD_SEC', 'Unknown', 'INVALID_SECURITY')],
        [NetFieldError('IBM US Equity', 'BAD_FLD', 'BAD_FLD_CAT', 'Field not valid', 'SUB')])
    result = connect(monkeypatch, StubClient(response)).get_reference_data(
        ['BAD US Equity', 'IBM US Equity'],
        'BAD_FLD')
    assert result.request.security_errors == [
        SecurityError('BAD US Equity', 'BAD_SEC', 'Unknown', 'INVALID_SECURITY'),
        ]
    assert result.request.field_errors == [
        FieldError('IBM US Equity', 'BAD_FLD', 'BAD_FLD_CAT', 'Field not valid', 'SUB'),
        ]


def test_historical_frame_shape(monkeypatch):
    """Verify each security's frame is indexed by Date and named 'date',
    a null is NaN in a float column, and a security with no points has an
    empty frame with the requested columns.

    Mutation: the index name dropped, the dates left as .NET DateOnly, or
        a security with no points left out.
    Oracle: two hand-built points for IBM, the second null, and none for
        EMPTY.
    """
    response = historical_response({
        'IBM US Equity': [
            (DateOnly(2024, 1, 2), net_row(PX_LAST=161.5)),
            (DateOnly(2024, 1, 3), net_row(PX_LAST=None)),
            ],
        'EMPTY US Equity': [],
        })
    result = connect(monkeypatch, StubClient(response)).get_historical(
        ['IBM US Equity', 'EMPTY US Equity'],
        'PX_LAST')
    ibm_df = result.as_dict()['IBM US Equity']
    assert list(ibm_df.index) == [Date(2024, 1, 2), Date(2024, 1, 3)]
    assert ibm_df.index.name == 'date'
    assert ibm_df['PX_LAST'].dtype == 'float64'
    assert ibm_df['PX_LAST'].iloc[0] == 161.5
    assert math.isnan(ibm_df['PX_LAST'].iloc[1])
    empty_df = result.as_dict()['EMPTY US Equity']
    assert empty_df.empty
    assert list(empty_df.columns) == ['PX_LAST']
    assert list(result.as_dataframe().columns) == [
        ('IBM US Equity', 'PX_LAST'),
        ('EMPTY US Equity', 'PX_LAST'),
        ]


def test_historical_no_securities_gives_empty_frame(monkeypatch):
    """Verify as_dataframe with no securities is an empty DataFrame.

    Mutation: pd.concat called on no frames, which raises ValueError.
    Oracle: the Python blp's pd.DataFrame() for an empty response.
    """
    result = connect(monkeypatch, StubClient(historical_response({}))).get_historical(
        'BAD US Equity',
        'PX_LAST')
    assert result.as_dataframe().equals(pd.DataFrame())


def security_exception() -> RequestSecurityException:
    """RequestSecurityException with one BAD_SEC error.
    """
    errors = List[NetSecurityError]()
    errors.Add(NetSecurityError('BAD US Equity', 'BAD_SEC', 'Unknown', None))
    return RequestSecurityException(errors)


def field_exception() -> RequestFieldException:
    """RequestFieldException with one BAD_FLD error.
    """
    errors = List[NetFieldError]()
    errors.Add(NetFieldError('IBM US Equity', 'BAD_FIELD', 'BAD_FLD', 'Field not valid', None))
    return RequestFieldException(errors)


@pytest.mark.parametrize(('error', 'expected', 'text'), [
    (
        security_exception(),
        RequestSecurityError,
        str(SecurityError('BAD US Equity', 'BAD_SEC', 'Unknown', None)),
        ),
    (
        field_exception(),
        RequestFieldError,
        str(FieldError('IBM US Equity', 'BAD_FIELD', 'BAD_FLD', 'Field not valid', None)),
        ),
    (RequestTimeoutException('No data within the idle limit.'), RequestTimeoutError, None),
    (SessionException('Connectivity check failed.'), SessionError, None),
    ])
def test_errors_translate_to_python_classes(monkeypatch, error, expected, text):
    """Verify each blp-net exception raises its Python blp class, with the
    Python blp's text for a security or field error.

    Mutation: the security and field classes swapped, the error text left
        in C#'s record form, or a timeout raised as plain SessionError.
    Oracle: the Python blp's message, ''.join of each error tuple's str.
    """
    session = connect(monkeypatch, StubClient(error))
    with pytest.raises(expected) as excinfo:
        session.get_reference_data('IBM US Equity', 'PX_LAST')
    assert type(excinfo.value) is expected
    if text is not None:
        assert str(excinfo.value) == text


def test_response_error_is_runtime_error_outside_session_error(monkeypatch):
    """Verify ResponseException raises ResponseError, a RuntimeError that is
    no SessionError, with its category and subcategory.

    Mutation: ResponseError under SessionError, which a caller would read
        as a lost session, or category read from the subcategory.
    Oracle: the Python blp's tests, which assert RuntimeError for a
        RequestFailure and no SessionError for a responseError.
    """
    error = ResponseException('REQUEST FAILED: LIMIT', 'LIMIT', 'DAILY_LIMIT_REACHED')
    session = connect(monkeypatch, StubClient(error))
    with pytest.raises(ResponseError, match='^REQUEST FAILED: LIMIT$') as excinfo:
        session.get_reference_data('IBM US Equity', 'PX_LAST')
    assert isinstance(excinfo.value, RuntimeError)
    assert not isinstance(excinfo.value, SessionError)
    assert excinfo.value.category == 'LIMIT'
    assert excinfo.value.subcategory == 'DAILY_LIMIT_REACHED'


def test_other_dotnet_error_passes_through(monkeypatch):
    """Verify a .NET exception outside the Blp classes is raised as is.

    Mutation: a catch-all that turns every exception into SessionError.
    Oracle: System.ArgumentException, which no Blp class covers.
    """
    session = connect(monkeypatch, StubClient(System.ArgumentException('bad topic')))
    with pytest.raises(System.ArgumentException):
        session.get_reference_data('IBM US Equity', 'PX_LAST')


def test_ctrl_c_cancels_the_request(monkeypatch):
    """Verify Ctrl-C during a wait cancels the request's token and raises
    KeyboardInterrupt.

    Mutation: KeyboardInterrupt raised without canceling, which leaves the
        .NET request waiting out its idle limit.
    Oracle: a request that never completes, and a timer that interrupts
        the main thread.
    """
    client = StubClient()
    session = connect(monkeypatch, client)
    threading.Timer(0.1, _thread.interrupt_main).start()
    with pytest.raises(KeyboardInterrupt):
        session.get_reference_data('IBM US Equity', 'PX_LAST')
    assert client.tokens[0].IsCancellationRequested


def test_exit_disposes_once(monkeypatch):
    """Verify leaving a with block disposes the client, and the exit hook
    disposes only clients still open.

    Mutation: the exit hook skips a client never closed, or disposes a
        closed client a second time.
    Oracle: dispose counts on two stub clients, one closed by its block.
    """
    closed = StubClient()
    with connect(monkeypatch, closed):
        pass
    left_open = StubClient()
    connect(monkeypatch, left_open)
    dispose_live_clients()
    assert closed.dispose_cnt == 1
    assert left_open.dispose_cnt == 1


def row_list(*rows: Any) -> List[IReadOnlyDictionary[String, Object]]:
    """.NET row list holding rows, in order.
    """
    net_rows = List[IReadOnlyDictionary[String, Object]]()
    for row in rows:
        net_rows.Add(row)
    return net_rows


def test_intraday_range_is_utc_at_offset_zero(monkeypatch):
    """Verify a naive start is read as UTC, an aware end is converted to
    UTC, both go at offset 0, and microseconds are kept.

    Mutation: a naive value read in the host zone, the end's wall clock
        sent without conversion, a nonzero offset, or the microseconds
        dropped.
    Oracle: 10:00 at -05:00 is 15:00 UTC; the naive 09:30:00.000500 stays
        09:30:00.000500 UTC. The host-zone mutation fails only on a host
        outside UTC.
    """
    client = StubClient(row_list())
    connect(monkeypatch, client).get_intraday_tick(
        'IBM US Equity',
        start=datetime.datetime(2024, 1, 2, 9, 30, 0, 500),
        end='2024-01-02T10:00:00-05:00')
    request = client.requests[0]
    start = DateTimeOffset(2024, 1, 2, 9, 30, 0, TimeSpan.Zero).AddTicks(5000)
    assert request.Start == start
    assert request.End == DateTimeOffset(2024, 1, 2, 15, 0, 0, TimeSpan.Zero)
    assert request.Start.Offset == TimeSpan.Zero
    assert request.End.Offset == TimeSpan.Zero


def test_intraday_missing_start_is_a_day_before_end(monkeypatch):
    """Verify a missing start is one day before end, and an unreadable bound
    raises before any request.

    Mutation: start taken from now in place of end, DateTime.parse's None
        kept as the bound, or a missing end taken as other than now.
    Oracle: end 2024-01-02 00:00 UTC gives start 2024-01-01 00:00 UTC;
        'garbage' is a string DateTime.parse returns None for; the clock
        read either side of the call bounds a missing end.
    """
    client = StubClient(row_list())
    session = connect(monkeypatch, client)
    session.get_intraday_bar('IBM US Equity', start=pd.NaT, end='2024-01-02T00:00:00Z')
    start = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
    assert client.requests[0].Start == start
    with pytest.raises(ValueError, match="'garbage'"):
        session.get_intraday_bar('IBM US Equity', start='garbage', end='2024-01-02')
    assert len(client.requests) == 1
    before = DateTimeOffset.UtcNow
    session.get_intraday_bar('IBM US Equity')
    assert before <= client.requests[1].End <= DateTimeOffset.UtcNow


TICK_FLAGS = {
    'include_condition_codes': 'IncludeConditionCodes',
    'include_broker_codes': 'IncludeBrokerCodes',
    'include_equity_ref_price': 'IncludeEquityRefPrice',
    'include_action_codes': 'IncludeActionCodes',
    'include_indicator_codes': 'IncludeIndicatorCodes',
    'include_trade_date': 'IncludeTradeDate',
    'include_trade_time': 'IncludeTradeTime',
    'include_rsp_codes': 'IncludeRpsCodes',
    'include_bic_mic_codes': 'IncludeBicMicCodes',
    }


@pytest.mark.parametrize('keyword', list(TICK_FLAGS))
def test_tick_flag_sets_only_its_property(monkeypatch, keyword):
    """Verify each tick flag sets its own property and no other.

    Mutation: two flags crossed, such as include_rsp_codes set on
        IncludeBicMicCodes, or a flag not passed on.
    Oracle: one flag set per case, every other property expected False.
    """
    client = StubClient(row_list())
    connect(monkeypatch, client).get_intraday_tick('IBM US Equity', **{keyword: True})
    request = client.requests[0]
    assert {name: getattr(request, name) for name in TICK_FLAGS.values()} == {
        name: name == TICK_FLAGS[keyword] for name in TICK_FLAGS.values()
        }


def test_tick_events_map_to_members(monkeypatch):
    """Verify one event name or a list maps to IntradayEventType members in
    order, and None keeps the default of TRADE, ASK and BID.

    Mutation: a str iterated letter by letter, BID_BEST mapped to Bid, or
        an empty list sent for None.
    Oracle: the Python blp's events handling and C#'s default.
    """
    client = StubClient(row_list())
    session = connect(monkeypatch, client)
    session.get_intraday_tick('IBM US Equity', 'BID_BEST')
    session.get_intraday_tick('IBM US Equity', ['AT_TRADE', 'TRADE'])
    session.get_intraday_tick('IBM US Equity')
    assert list(client.requests[0].Events) == [IntradayEventType.BidBest]
    assert list(client.requests[1].Events) == [
        IntradayEventType.AtTrade,
        IntradayEventType.Trade,
        ]
    assert list(client.requests[2].Events) == [
        IntradayEventType.Trade,
        IntradayEventType.Ask,
        IntradayEventType.Bid,
        ]


@pytest.mark.parametrize(('method', 'args', 'kwargs'), [
    ('get_intraday_tick', ('IBM US Equity',), {'return_eids': True}),
    ('get_intraday_tick', ('IBM US Equity', None, None, None, 'UTC', None, True), {}),
    ('get_intraday_bar', ('IBM US Equity',), {'gap_fill_initial_bar': True}),
    ('get_screener', ('Core Capital Ratios',), {'language': 'ENGLISH'}),
    ])
def test_intraday_and_screener_gaps_raise(monkeypatch, method, args, kwargs):
    """Verify a Python blp flag that blp-net lacks, by keyword or by
    position, raises TypeError and sends nothing.

    Mutation: a **kwargs catch-all that drops the flag, or a positional
        slot left where include_nonplottable_events stood.
    Oracle: the stub client records no request.
    """
    client = StubClient(row_list())
    with pytest.raises(TypeError):
        getattr(connect(monkeypatch, client), method)(*args, **kwargs)
    assert client.requests == []


@pytest.mark.parametrize(('interval', 'expected'), [(None, 1), (0, 1), (5, 5)])
def test_bar_interval_and_event(monkeypatch, interval, expected):
    """Verify a missing or zero interval sends 1 and the event maps to its
    member.

    Mutation: interval 0 sent as given, which Bloomberg rejects, the event
        left at its Trade default, or the zone not set.
    Oracle: the Python blp's interval or 1, and BID for Bid.
    """
    client = StubClient(row_list())
    session = connect(monkeypatch, client)
    session.get_intraday_bar(
        'IBM US Equity',
        'BID',
        timezone='Asia/Tokyo',
        interval=interval)
    assert client.requests[0].Interval == expected
    assert client.requests[0].Event == IntradayEventType.Bid
    assert client.requests[0].TimeZone.Id == 'Asia/Tokyo'


def test_intraday_frame_shape(monkeypatch):
    """Verify ticks become a frame in row order with the time column in the
    request zone and NaN for a null, as_dict refuses, and no ticks gives an
    empty frame.

    Mutation: the time left as a .NET DateTimeOffset or read in UTC, the
        zone or the limits not set on the request, a null kept as None, or
        as_dict returning the rows.
    Oracle: two hand-built ticks at 09:30 and 09:31 at -05:00, 23:30 and
        23:31 in Tokyo, the second with no size.
    """
    rows = row_list(
        net_row(
            time=DateTimeOffset(2024, 1, 2, 9, 30, 0, TimeSpan.FromHours(-5)),
            type='TRADE',
            value=161.5,
            size=100),
        net_row(
            time=DateTimeOffset(2024, 1, 2, 9, 31, 0, TimeSpan.FromHours(-5)),
            type='BID',
            value=161.4,
            size=None))
    client = StubClient(rows)
    result = connect(monkeypatch, client).get_intraday_tick(
        'IBM US Equity',
        timezone='Asia/Tokyo')
    df = result.as_dataframe()
    assert list(df.columns) == ['time', 'type', 'value', 'size']
    assert df['time'].tolist() == [
        DateTime(2024, 1, 2, 23, 30, tzinfo=TOKYO),
        DateTime(2024, 1, 2, 23, 31, tzinfo=TOKYO),
        ]
    assert str(df['time'].dt.tz) == 'Asia/Tokyo'
    assert client.requests[0].TimeZone.Id == 'Asia/Tokyo'
    assert client.requests[0].IdleTimeout == TimeSpan.FromSeconds(120)
    assert df['size'].iloc[0] == 100
    assert math.isnan(df['size'].iloc[1])
    with pytest.raises(NotImplementedError):
        result.as_dict()
    empty_session = connect(monkeypatch, StubClient(row_list()))
    empty = empty_session.get_intraday_bar('IBM US Equity')
    assert empty.as_dataframe().equals(pd.DataFrame())


def test_screener_request_options(monkeypatch):
    """Verify get_screener sends the group as given, the screen type's
    member and asof as its calendar date.

    Mutation: a None group replaced by 'General', PRIVATE mapped to Global,
        asof taken after conversion to UTC, an empty asof read as a date,
        the zone not set, or a None type raising AttributeError.
    Oracle: '2024-01-02T20:00:00-05:00' names January 2 where it was
        written; the Python blp read an empty asof as none.
    """
    client = StubClient(EqsResponse(
        Dictionary[String, IReadOnlyDictionary[String, Object]](),
        List[NetSecurityError](),
        List[NetFieldError]()))
    session = connect(monkeypatch, client)
    session.get_screener(
        'Test Screen',
        None,
        'PRIVATE',
        '2024-01-02T20:00:00-05:00',
        timezone='Asia/Tokyo')
    request = client.requests[0]
    assert request.ScreenName == 'Test Screen'
    assert request.Group is None
    assert request.ScreenType == EqsScreenType.Private
    assert request.AsOf == DateOnly(2024, 1, 2)
    assert request.TimeZone.Id == 'Asia/Tokyo'
    session.get_screener('Test Screen', asof='')
    assert client.requests[1].AsOf is None
    with pytest.raises(ValueError):
        session.get_screener('Test Screen', type=None)
    assert len(client.requests) == 2


def test_screener_frame_shape(monkeypatch):
    """Verify the screener frame keeps securities in arrival order with the
    union of columns in first-seen order, and errors sit on
    response.request.

    Mutation: securities sorted, columns limited to the first security's,
        or the errors left off response.request.
    Oracle: ZZZ before AAA, AAA with a column ZZZ lacks, one security
        error.
    """
    values = Dictionary[String, IReadOnlyDictionary[String, Object]]()
    values['ZZZ US Equity'] = net_row(Ticker='ZZZ', Price=2.5)
    values['AAA US Equity'] = net_row(Ticker='AAA', Price=None, Sector='Tech')
    security_errors = List[NetSecurityError]()
    security_errors.Add(NetSecurityError('BAD US Equity', 'BAD_SEC', 'Unknown', None))
    response = EqsResponse(values, security_errors, List[NetFieldError]())
    result = connect(monkeypatch, StubClient(response)).get_screener('Test Screen')
    df = result.as_dataframe()
    assert list(df.index) == ['ZZZ US Equity', 'AAA US Equity']
    assert list(df.columns) == ['Ticker', 'Price', 'Sector']
    assert math.isnan(df.loc['AAA US Equity', 'Price'])
    assert result.request.security_errors == [
        SecurityError('BAD US Equity', 'BAD_SEC', 'Unknown', None),
        ]


def bql_response(results: dict[str, list], errors: list[str]) -> BqlResponse:
    """blp-net BqlResponse with each result's rows, in dict order.
    """
    row_type = IReadOnlyList[IReadOnlyDictionary[String, Object]]
    net_results = Dictionary[String, row_type]()
    for name, rows in results.items():
        net_results[name] = row_list(*rows)
    net_errors = List[String]()
    for error in errors:
        net_errors.Add(error)
    return BqlResponse(net_results, net_errors)


def test_bql_expression_from_universe_and_fields(monkeypatch):
    """Verify universe and fields build the query when no expression is
    given, and a call with neither raises before any request.

    Mutation: the get() and for() clauses swapped, or a None expression
        sent on.
    Oracle: the Python blp's 'get({fields}) for({universe})'.
    """
    client = StubClient(bql_response({}, []))
    session = connect(monkeypatch, client)
    session.bql(universe="['IBM US Equity']", fields='px_last')
    assert client.requests[0].Expression == "get(px_last) for(['IBM US Equity'])"
    with pytest.raises(ValueError):
        session.bql(universe="['IBM US Equity']")
    assert len(client.requests) == 1


def test_bql_exception_raises_when_asked(monkeypatch):
    """Verify a BQL exception raises RequestSecurityError with the joined
    messages when raise_security_error is set, and is listed otherwise.

    Mutation: the flag ignored, or the messages joined with a separator.
    Oracle: the Python blp's ''.join of the messages.
    """
    response = bql_response({}, ['Bad universe.', 'Bad field.'])
    session = connect(monkeypatch, StubClient(response))
    with pytest.raises(RequestSecurityError, match='^Bad universe.Bad field.$'):
        session.bql('get(px_last) for(bad)', raise_security_error=True)
    result = session.bql('get(px_last) for(bad)')
    assert result.request.security_errors == ['Bad universe.', 'Bad field.']


def test_bql_frames_merge_on_shared_columns(monkeypatch):
    """Verify each result is a frame with its values converted, and
    as_dataframe outer-merges the frames on their shared columns.

    Mutation: a DATE left as a .NET DateOnly, a merge on every column, or
        an inner merge that drops the security only one result holds.
    Oracle: two results sharing ID and DATE, MSFT in only the second.
    """
    response = bql_response({
        'px_last': [
            net_row(ID='IBM US Equity', px_last=161.5, DATE=DateOnly(2024, 1, 2)),
            ],
        'name': [
            net_row(ID='IBM US Equity', name='IBM', DATE=DateOnly(2024, 1, 2)),
            net_row(ID='MSFT US Equity', name='MSFT', DATE=DateOnly(2024, 1, 2)),
            ],
        }, [])
    result = connect(monkeypatch, StubClient(response)).bql('get(px_last, name) for(x)')
    assert result.as_dict()['px_last']['DATE'].iloc[0] == Date(2024, 1, 2)
    df = result.as_dataframe()
    assert list(df.columns) == ['ID', 'px_last', 'DATE', 'name']
    assert df['ID'].tolist() == ['IBM US Equity', 'MSFT US Equity']
    assert df['name'].tolist() == ['IBM', 'MSFT']
    assert math.isnan(df['px_last'].iloc[1])


def test_bql_empty_result_keeps_its_columns(monkeypatch):
    """Verify a result with no rows keeps its 'ID' and result-name columns,
    so the merge still takes it.

    Mutation: an empty result built with no columns, which as_dataframe
        drops, or which empties the merge when it comes first.
    Oracle: the Python blp's frame of the ID and value columns, and an
        outer merge that holds px_last as NaN for both securities.
    """
    response = bql_response({
        'px_last': [],
        'name': [
            net_row(ID='IBM US Equity', name='IBM'),
            net_row(ID='MSFT US Equity', name='MSFT'),
            ],
        }, [])
    result = connect(monkeypatch, StubClient(response)).bql('get(px_last, name) for(x)')
    assert list(result.as_dict()['px_last'].columns) == ['ID', 'px_last']
    df = result.as_dataframe()
    assert df['ID'].tolist() == ['IBM US Equity', 'MSFT US Equity']
    assert df['px_last'].isna().all()
    assert result.request.field_errors == []
