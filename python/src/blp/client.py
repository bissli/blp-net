"""Bloomberg requests through blp-net, returned as pandas and opendate values.
"""
import atexit
import datetime
import logging
from collections import defaultdict, namedtuple
from collections.abc import Callable, Iterator
from contextlib import contextmanager
from dataclasses import dataclass
from types import SimpleNamespace
from typing import Any, Self

import clr
import numpy as np
import pandas as pd
import System
from Blp import BlpClient, BlpClientOptions, IntradayEventType
from Blp import RequestFieldException, RequestSecurityException
from Blp import RequestTimeoutException, ResponseException
from Blp import SessionCreateException, SessionException
from Blp import SessionNotAvailableException, SessionStartException
from Blp import SessionTerminatedException
from Blp.Bql import BqlRequest
from Blp.Eqs import EqsRequest, EqsScreenType
from Blp.HistoricalData import HistoricalDataRequest, NonTradingDayFillMethod
from Blp.HistoricalData import NonTradingDayFillOption, OverrideOption
from Blp.HistoricalData import Periodicity, PeriodicityAdjustment
from Blp.HistoricalData import PricingOption
from Blp.IntradayBars import IntradayBarRequest
from Blp.IntradayTicks import IntradayTickRequest
from Blp.ReferenceData import ReferenceDataRequest
from Blp.Subscriptions import CallbackLogger
from Blp.Subscriptions import Subscription as NetSubscription
from Blp.Subscriptions import SubscriptionOptions
from Microsoft.Extensions.Logging import LogLevel
from opendate import LCL, UTC, Date, DateTime, Time, Timezone
from System import Action, DateOnly, DateTimeOffset, IAsyncResult, Object
from System import String, TimeOnly, TimeSpan, TimeZoneInfo
from System.Collections.Generic import Dictionary, IReadOnlyDictionary, List
from System.Threading import CancellationToken, CancellationTokenSource
from System.Threading import Timeout
from System.Threading.Tasks import Task

__all__ = [
    'Blp',
    'SessionError',
    'SessionCreateError',
    'SessionStartError',
    'SessionTerminatedError',
    'SessionNotAvailableError',
    'RequestTimeoutError',
    'RequestSecurityError',
    'RequestFieldError',
    'ResponseError',
    'SecurityError',
    'FieldError',
    'HistoricalDataResponse',
    'ReferenceDataResponse',
    'IntradayResponse',
    'EQSResponse',
    'BQLResponse',
    'Subscription',
    'SubscriptionStatus',
    'TopicState',
    ]

REQUEST_IDLE_SECONDS = 120
WAIT_STEP_MS = 500

SecurityError = namedtuple(
    'securityError',
    ['security', 'category', 'message', 'subcategory'])
FieldError = namedtuple(
    'fieldError',
    ['security', 'field', 'category', 'message', 'subcategory'])


class SessionError(RuntimeError):
    """The Bloomberg session failed, or cannot serve a request.
    """


class SessionCreateError(SessionError):
    """The Bloomberg session failed to start.
    """


class SessionStartError(SessionError):
    """A Bloomberg service failed to open.
    """


class SessionTerminatedError(SessionError):
    """The Bloomberg session terminated.
    """


class SessionNotAvailableError(SessionError):
    """No usable Bloomberg session on this host, such as a terminal not
    logged in.
    """


class RequestTimeoutError(SessionError):
    """A request passed its idle_seconds or total_seconds limit.
    """


class RequestSecurityError(Exception):
    """A request set to raise on a securityError received one.
    """


class RequestFieldError(Exception):
    """A request set to raise on a fieldExceptions entry received one.
    """


class ResponseError(RuntimeError):
    """Bloomberg failed a request, with a responseError element or a
    RequestFailure status.

    Parameters
    ----------
    message : str
        Bloomberg's report of the failure.
    category : str, optional
        The error's category, such as LIMIT. None when it has none.
    subcategory : str, optional
        The error's subcategory. None when it has none.

    Attributes
    ----------
    category : str or None
    subcategory : str or None
    """

    def __init__(
        self,
        message: str,
        category: str | None = None,
        subcategory: str | None = None) -> None:
        super().__init__(message)
        self.category = category
        self.subcategory = subcategory


SESSION_ERROR_BY_TYPE = {
    RequestTimeoutException: RequestTimeoutError,
    SessionCreateException: SessionCreateError,
    SessionStartException: SessionStartError,
    SessionTerminatedException: SessionTerminatedError,
    SessionNotAvailableException: SessionNotAvailableError,
    }


@contextmanager
def translated_errors() -> Iterator[None]:
    """Raise each blp-net exception as its Python blp class.

    Raises
    ------
    RequestSecurityError
        For RequestSecurityException. The text joins the str of each
        SecurityError, as the Python blp wrote it.
    RequestFieldError
        For RequestFieldException, with text as above.
    ResponseError
        For ResponseException, with its category and subcategory.
    SessionError
        For SessionException, or the subclass that matches its type.

    Notes
    -----
    - Every other .NET exception passes through as pythonnet raised it.
    """
    try:
        yield
    except RequestSecurityException as error:
        errors = security_errors(error.Errors)
        raise RequestSecurityError(''.join(str(e) for e in errors)) from error
    except RequestFieldException as error:
        errors = field_errors(error.Errors)
        raise RequestFieldError(''.join(str(e) for e in errors)) from error
    except ResponseException as error:
        raise ResponseError(
            error.Message,
            error.Category,
            error.Subcategory) from error
    except SessionException as error:
        session_error = SESSION_ERROR_BY_TYPE.get(type(error), SessionError)
        raise session_error(error.Message) from error


def wait_for(start: Callable[[CancellationToken], Task]) -> Any:
    """Result of the task start returns, canceled by Ctrl-C.

    Parameters
    ----------
    start : Callable[[CancellationToken], Task]
        Starts the .NET call with the token it must honor.

    Returns
    -------
    Any
        The task's result.

    Raises
    ------
    KeyboardInterrupt
        Ctrl-C during the call, after the token is canceled. A result that
        is IDisposable and already complete is disposed.
    SessionError, ResponseError, RequestSecurityError, RequestFieldError
        As translated_errors raises them.
    """
    source = CancellationTokenSource()
    task = None
    with translated_errors():
        try:
            task = start(source.Token)
            handle = IAsyncResult(task).AsyncWaitHandle
            # Python sees Ctrl-C only after a .NET wait returns.
            while not handle.WaitOne(WAIT_STEP_MS):
                pass
            return task.GetAwaiter().GetResult()
        except KeyboardInterrupt:
            source.Cancel()
            if (task is not None
                and task.IsCompletedSuccessfully
                and isinstance(task.Result, System.IDisposable)):
                task.Result.Dispose()
            raise


def calendar_date(value: datetime.date | str) -> Date:
    """The calendar date value names in its own timezone.

    Parameters
    ----------
    value : datetime.date or str
        A date, a datetime or pandas Timestamp, naive or aware, or a string
        Date.parse reads. An offset in the value never shifts the date, so
        '2024-01-02T20:00-05:00' is 2024-01-02.

    Returns
    -------
    Date

    Raises
    ------
    ValueError
        Date.parse cannot read the string.
    """
    if isinstance(value, datetime.date):
        return Date.instance(value)
    date = Date.parse(value)
    if date is None:
        raise ValueError(f'No date in {value!r}')
    return date


def to_date_only(value: datetime.date | str | None) -> DateOnly | None:
    """DateOnly of the calendar date value names, or None for None, NaN,
    NaT or pd.NA.

    Raises
    ------
    ValueError
        As calendar_date raises it.
    """
    if pd.isna(value):
        return None
    date = calendar_date(value)
    return DateOnly(date.year, date.month, date.day)


def utc_instant(value: datetime.date | str) -> DateTime:
    """The instant value names, in UTC.

    Parameters
    ----------
    value : datetime.date or str
        A date, a datetime or pandas Timestamp, or a string DateTime.parse
        reads. A naive value, or a date, is read as UTC.

    Returns
    -------
    DateTime

    Raises
    ------
    ValueError
        DateTime.parse cannot read the string.
    """
    if isinstance(value, datetime.date):
        return DateTime.instance(value).in_timezone(UTC)
    instant = DateTime.parse(value)
    if instant is None:
        raise ValueError(f'No date-time in {value!r}')
    return instant.in_timezone(UTC)


def intraday_range(
    start: datetime.date | str | None,
    end: datetime.date | str | None) -> tuple[DateTimeOffset, DateTimeOffset]:
    """Start and end of an intraday request, each at offset 0.

    Parameters
    ----------
    start : datetime.date, str or None
        As utc_instant reads it. None, NaN or NaT means one day before end.
    end : datetime.date, str or None
        As utc_instant reads it. None, NaN or NaT means now.

    Returns
    -------
    tuple[DateTimeOffset, DateTimeOffset]
        (start, end), microseconds kept.

    Raises
    ------
    ValueError
        As utc_instant raises it.
    """
    end_utc = DateTime.now(UTC) if pd.isna(end) else utc_instant(end)
    start_utc = end_utc.subtract(days=1) if pd.isna(start) else utc_instant(start)
    # An offset of 0: blp-net converts a DateTime of Kind Local at the
    # host's offset.
    return tuple(
        DateTimeOffset(
            instant.year,
            instant.month,
            instant.day,
            instant.hour,
            instant.minute,
            instant.second,
            TimeSpan.Zero).AddTicks(instant.microsecond * 10)
        for instant in (start_utc, end_utc))


def to_enum(enum_type: type, name: str, prefix: str = '') -> Any:
    """Member of a blp-net enum for its Bloomberg name.

    Parameters
    ----------
    enum_type : type
        The .NET enum, such as Periodicity.
    name : str
        Bloomberg name, such as 'SEMI_ANNUALLY'. prefix is optional on it.
    prefix : str, default ''
        Bloomberg prefix the C# member leaves out, such as
        'OVERRIDE_OPTION_'.

    Returns
    -------
    Any
        The enum member: 'SEMI_ANNUALLY' gives SemiAnnually.

    Raises
    ------
    ValueError
        The name has no member.
    """
    member = ''.join(
        part.capitalize() for part in name.removeprefix(prefix).split('_'))
    if member not in System.Enum.GetNames(clr.GetClrType(enum_type)):
        raise ValueError(f'{name!r} is not a {enum_type.__name__} name')
    return getattr(enum_type, member)


def to_string_list(values: str | list[str]) -> List[String]:
    """.NET string list of values, with one str as a one-item list.
    """
    strings = List[String]()
    for value in [values] if isinstance(values, str) else values:
        strings.Add(str(value))
    return strings


def to_overrides(overrides: dict[str, Any]) -> Dictionary[String, String]:
    """.NET field overrides, each value as its str.
    """
    strings = Dictionary[String, String]()
    for field, value in overrides.items():
        strings[field] = str(value)
    return strings


def reject_gaps(overrides: dict[str, Any], gaps: set[str], method: str) -> None:
    """Refuse a Python blp keyword that blp-net lacks.

    Parameters
    ----------
    overrides : dict[str, Any]
        The call's other keywords, each bound for a field override.
    gaps : set[str]
        The method's Python blp keywords with no blp-net counterpart.
    method : str
        The method name, for the message.

    Raises
    ------
    TypeError
        A key of overrides is in gaps. Sent on, it would become a field
        override that Bloomberg rejects or ignores.
    """
    if unsupported := sorted(overrides.keys() & gaps):
        raise TypeError(f'{method}() keywords {unsupported} have no blp-net counterpart')


def to_python(value: Any, zone: Timezone) -> Any:
    """Python value of one blp-net response value.

    Parameters
    ----------
    value : Any
        The value as pythonnet hands it over. A float, int, bool or str
        arrives converted.
    zone : Timezone
        The request's zone. A DateTimeOffset or TimeOnly from blp-net is
        already in it.

    Returns
    -------
    Any
        np.nan for null. Date for DateOnly. DateTime in zone for
        DateTimeOffset. Time with tzinfo zone for TimeOnly. bytes for a
        byte array. A DataFrame for bulk rows, columns in first-seen order,
        each cell converted. Anything else as given.
    """
    if value is None:
        return np.nan
    # Checked before the .NET types: an isinstance test against a
    # pythonnet class is slow, and most values arrive as plain floats.
    if isinstance(value, (float, int, str)):
        return value
    if isinstance(value, DateOnly):
        return Date(value.Year, value.Month, value.Day)
    if isinstance(value, DateTimeOffset):
        utc = value.UtcDateTime
        return DateTime(
            utc.Year,
            utc.Month,
            utc.Day,
            utc.Hour,
            utc.Minute,
            utc.Second,
            (value.UtcTicks % TimeSpan.TicksPerSecond) // 10,
            tzinfo=UTC).in_timezone(zone)
    if isinstance(value, TimeOnly):
        return Time(
            value.Hour,
            value.Minute,
            value.Second,
            (value.Ticks % TimeSpan.TicksPerSecond) // 10,
            tzinfo=zone)
    if isinstance(value, System.Array[System.Byte]):
        return bytes(value)
    if isinstance(value, System.Collections.IEnumerable):
        return pd.DataFrame([
            {pair.Key: to_python(pair.Value, zone) for pair in row}
            for row in value
            ])
    return value


def security_errors(errors: Any) -> list[SecurityError]:
    """SecurityError tuples of a blp-net SecurityError list, in order.
    """
    return [
        SecurityError(e.Security, e.Category, e.Message, e.Subcategory)
        for e in errors
        ]


def field_errors(errors: Any) -> list[FieldError]:
    """FieldError tuples of a blp-net FieldError list, in order.
    """
    return [
        FieldError(e.Security, e.Field, e.Category, e.Message, e.Subcategory)
        for e in errors
        ]


class ReferenceDataResponse:
    """Reference data (BDP) values per security.

    Parameters
    ----------
    response : Blp.ReferenceData.ReferenceDataResponse
        The blp-net response.
    fields : list[str]
        Requested fields, in request order.
    zone : Timezone
        The request's zone.
    force_string : bool
        The request set ForceString.

    Attributes
    ----------
    request : SimpleNamespace
        security_errors, the securities Bloomberg rejected, and
        field_errors, the fields it rejected per security, where the
        Python blp's request object held them.
    """

    def __init__(
        self,
        response: Any,
        fields: list[str],
        zone: Timezone,
        force_string: bool) -> None:
        self.fields = fields
        self.force_string = force_string
        self.response_map: defaultdict[str, dict] = defaultdict(dict)
        for pair in response.Values:
            self.response_map[pair.Key] = {
                field: to_python(pair.Value[field], zone) for field in fields
                }
        self.request = SimpleNamespace(
            security_errors=security_errors(response.SecurityErrors),
            field_errors=field_errors(response.FieldErrors))

    def as_dict(self) -> defaultdict[str, dict]:
        """Security to field to value, securities in arrival order.
        """
        return self.response_map

    def as_dataframe(self) -> pd.DataFrame:
        """One row per security in arrival order, one column per requested
        field in request order. Under force_string every cell is a str or
        None.
        """
        data = {ticker: pd.Series(row) for ticker, row in self.response_map.items()}
        df = pd.DataFrame.from_dict(data, orient='index')
        df = df.reindex(self.fields, axis=1)
        if self.force_string:
            df = df.astype(object).where(df.notna(), None)
        return df


class HistoricalDataResponse:
    """Historical data (BDH) points per security.

    Parameters
    ----------
    response : Blp.HistoricalData.HistoricalDataResponse
        The blp-net response.
    fields : list[str]
        Requested fields, in request order.
    zone : Timezone
        The request's zone.

    Attributes
    ----------
    request : SimpleNamespace
        security_errors, the securities Bloomberg rejected, and
        field_errors, the fields it rejected per security, where the
        Python blp's request object held them.
    """

    def __init__(self, response: Any, fields: list[str], zone: Timezone) -> None:
        self.response_map: dict[str, pd.DataFrame] = {}
        for pair in response.Rows:
            points = list(pair.Value)
            if not points:
                self.response_map[pair.Key] = pd.DataFrame(columns=fields)
                continue
            dates = [to_python(point.Date, zone) for point in points]
            values_by_field = {
                field: [to_python(point.Values[field], zone) for point in points]
                for field in fields
                }
            df = pd.DataFrame(values_by_field, columns=fields, index=dates)
            df.index.name = 'date'
            self.response_map[pair.Key] = df
        self.request = SimpleNamespace(
            security_errors=security_errors(response.SecurityErrors),
            field_errors=field_errors(response.FieldErrors))

    def as_dict(self) -> dict[str, pd.DataFrame]:
        """Security to its frame, indexed by Date and named 'date', one
        column per requested field. A security with no points has an empty
        frame with no index name.
        """
        return self.response_map

    def as_dataframe(self) -> pd.DataFrame:
        """Every security's frame side by side, columns (security, field).
        The index is the union of dates in first-seen order, unsorted.
        """
        if not self.response_map:
            return pd.DataFrame()
        return pd.concat(
            self.response_map.values(),
            keys=self.response_map.keys(),
            axis=1)


class IntradayResponse:
    """Intraday ticks or bars of one security, in time order.

    Parameters
    ----------
    rows : IReadOnlyList[IReadOnlyDictionary[str, object]]
        The blp-net rows. Every row holds every name any row has.
    zone : Timezone
        The request's zone.

    Attributes
    ----------
    rows : list[dict]
        One dict per tick or bar, each value converted by to_python.
    """

    def __init__(self, rows: Any, zone: Timezone) -> None:
        self.rows = [
            {pair.Key: to_python(pair.Value, zone) for pair in row}
            for row in rows
            ]

    def as_dict(self) -> dict:
        """Not supported: a row list has no key to index it by.

        Raises
        ------
        NotImplementedError
            Always.
        """
        raise NotImplementedError('Intraday rows have no dict form; use as_dataframe')

    def as_dataframe(self) -> pd.DataFrame:
        """One row per tick or bar under a RangeIndex. No rows gives an
        empty frame with no columns.
        """
        return pd.DataFrame.from_records(self.rows)


class EQSResponse:
    """The securities an EQS screen selects, with the screen's columns.

    Parameters
    ----------
    response : Blp.Eqs.EqsResponse
        The blp-net response.
    zone : Timezone
        The request's zone.

    Attributes
    ----------
    request : SimpleNamespace
        security_errors and field_errors, as on ReferenceDataResponse.
    """

    def __init__(self, response: Any, zone: Timezone) -> None:
        self.response_map: defaultdict[str, dict] = defaultdict(dict)
        for pair in response.Values:
            self.response_map[pair.Key] = {
                column.Key: to_python(column.Value, zone) for column in pair.Value
                }
        self.request = SimpleNamespace(
            security_errors=security_errors(response.SecurityErrors),
            field_errors=field_errors(response.FieldErrors))

    def as_dict(self) -> defaultdict[str, dict]:
        """Security to column to value, securities in arrival order.
        """
        return self.response_map

    def as_dataframe(self) -> pd.DataFrame:
        """One row per security in arrival order, columns the union of the
        screen's columns in first-seen order.
        """
        data = {ticker: pd.Series(row) for ticker, row in self.response_map.items()}
        return pd.DataFrame.from_dict(data, orient='index')


class BQLResponse:
    """BQL results, one frame per result name.

    Parameters
    ----------
    response : Blp.Bql.BqlResponse
        The blp-net response.

    Attributes
    ----------
    request : SimpleNamespace
        security_errors, the message of each exception Bloomberg reported,
        as str, and field_errors, always empty, as in the Python blp.
    """

    def __init__(self, response: Any) -> None:
        self.response_map: dict[str, pd.DataFrame] = {}
        for pair in response.Results:
            rows = [
                {column.Key: to_python(column.Value, UTC) for column in row}
                for row in pair.Value
                ]
            # Without its columns an empty result drops out of the merge.
            self.response_map[pair.Key] = (
                pd.DataFrame(rows) if rows else pd.DataFrame(columns=['ID', pair.Key]))
        self.request = SimpleNamespace(
            security_errors=list(response.Errors),
            field_errors=[])

    def as_dict(self) -> dict[str, pd.DataFrame]:
        """Result name to its frame: columns 'ID', the result name, then the
        secondary columns. A DATE value is a Date and a null is NaN. A
        result with no rows has only the 'ID' and result-name columns.
        """
        return self.response_map

    def as_dataframe(self) -> pd.DataFrame:
        """The frames outer-merged in order on the columns they share. A
        frame that shares no column with the merge so far is dropped.
        """
        if not self.response_map:
            return pd.DataFrame()
        frames = list(self.response_map.values())
        result = frames[0]
        for df in frames[1:]:
            common_columns = [
                column for column in result.columns if column in df.columns
                ]
            if common_columns:
                result = result.merge(df, on=common_columns, how='outer')
        return result


logger = logging.getLogger(__name__)

live_clients: set = set()


@atexit.register
def dispose_live_clients() -> None:
    """Stop the session of every Blp and Subscription not yet closed.
    """
    while live_clients:
        live_clients.pop().Dispose()


class Blp:
    """A Bloomberg session on localhost:8194 with OS logon, checked at
    construction with one reference data request.

    Parameters
    ----------
    application_identity_key : str, optional
        Bloomberg application identity key.
    idle_seconds : float, default REQUEST_IDLE_SECONDS
        Longest wait for a request's next event. Each partial response
        restarts the count. Read at each request.
    total_seconds : float, optional
        Longest wait for a request's full response. None sets no limit.
        Read at each request.

    Raises
    ------
    SessionCreateError
        The session failed to start.
    SessionStartError
        //blp/refdata failed to open.
    SessionError
        The connectivity check failed.

    Notes
    -----
    - The Python blp's host, port, auth, session and skip_test have no
      blp-net counterpart. Every parameter is keyword-only, so a
      positional host raises TypeError.
    - Leaving a with block stops the session. An exit hook stops any
      session still running.
    - Topic failures, connection and event-queue warnings and on_tick
      errors go to the blp.client logger.
    """

    def __init__(
        self,
        *,
        application_identity_key: str | None = None,
        idle_seconds: float = REQUEST_IDLE_SECONDS,
        total_seconds: float | None = None) -> None:
        self.idle_seconds = idle_seconds
        self.total_seconds = total_seconds
        options = BlpClientOptions()
        options.ApplicationIdentityKey = application_identity_key
        options.IdleTimeout = TimeSpan.FromSeconds(idle_seconds)
        if total_seconds is not None:
            options.TotalTimeout = TimeSpan.FromSeconds(total_seconds)
        self.client = wait_for(lambda token: BlpClient.ConnectAsync(options, token))
        live_clients.add(self.client)

    def __enter__(self) -> Self:
        return self

    def __exit__(self, exc_type: type | None, exc_value: BaseException | None, tb: Any) -> None:
        live_clients.discard(self.client)
        self.client.Dispose()

    def get_reference_data(
        self,
        tickers: str | list[str],
        flds: str | list[str],
        timezone: str = LCL.name,
        *,
        raise_security_error: bool = False,
        raise_field_error: bool = False,
        force_string: bool = False,
        time_as_datetime: bool = False,
        **overrides: Any) -> ReferenceDataResponse:
        """Equivalent of Excel BDP Request.

        Parameters
        ----------
        tickers : str or list[str]
            Bloomberg security identifier(s).
        flds : str or list[str]
            Bloomberg field name(s).
        timezone : str
            IANA zone for datetime and time values in the response.
        raise_security_error : bool, default False
            Raise RequestSecurityError for an invalid ticker.
        raise_field_error : bool, default False
            Raise RequestFieldError for an invalid field.
        force_string : bool, default False
            Return every value as a string, and a bulk field as JSON.
        time_as_datetime : bool, default False
            Return a time-only value as a datetime dated with this host's
            date.
        **overrides
            Bloomberg field overrides, field id to value, each sent as its
            str.

        Returns
        -------
        ReferenceDataResponse

        Raises
        ------
        TypeError
            decimal_places, field_parse_custom or return_formatted_value,
            which blp-net lacks.
        RequestSecurityError, RequestFieldError
            As raise_security_error and raise_field_error set.
        ResponseError
            Bloomberg failed the request.
        SessionError
            The session failed, or a limit passed (RequestTimeoutError).
        """
        reject_gaps(
            overrides,
            {'decimal_places', 'field_parse_custom', 'return_formatted_value'},
            'get_reference_data')
        zone = Timezone(timezone)
        fields = list(to_string_list(flds))
        request = ReferenceDataRequest(to_string_list(tickers), to_string_list(fields))
        request.TimeZone = TimeZoneInfo.FindSystemTimeZoneById(timezone)
        request.RaiseSecurityError = raise_security_error
        request.RaiseFieldError = raise_field_error
        request.ForceString = force_string
        request.TimeAsDateTime = time_as_datetime
        request.Overrides = to_overrides(overrides)
        self._apply_limits(request)
        response = wait_for(
            lambda token: self.client.GetReferenceDataAsync(request, token))
        return ReferenceDataResponse(response, fields, zone, force_string)

    def get_historical(
        self,
        tickers: str | list[str],
        flds: str | list[str],
        start: datetime.date | str | None = None,
        end: datetime.date | str | None = None,
        timezone: str = LCL.name,
        period: str | None = None,
        raise_security_error: bool = False,
        raise_field_error: bool = False,
        *,
        period_adjustment: str | None = None,
        currency: str | None = None,
        override_option: str | None = None,
        pricing_option: str | None = None,
        non_trading_day_fill_option: str | None = None,
        non_trading_day_fill_method: str | None = None,
        max_data_points: int | None = None,
        adjustment_normal: bool | None = None,
        adjustment_abnormal: bool | None = None,
        adjustment_split: bool | None = None,
        adjustment_follow_DPDF: bool | None = None,
        calendar_code_override: str | None = None,
        **overrides: Any) -> HistoricalDataResponse:
        """Equivalent of Excel BDH Request.

        Parameters
        ----------
        tickers : str or list[str]
            Bloomberg security identifier(s).
        flds : str or list[str]
            Bloomberg field name(s).
        start : datetime.date, str or None
            First date, as calendar_date reads it. None, NaN or NaT means
            the day before end.
        end : datetime.date, str or None
            Last date, read as start is. None, NaN or NaT means this host's
            date today.
        timezone : str
            IANA zone for datetime values in the response. The request
            dates ignore it.
        period : str, optional
            DAILY (the default), WEEKLY, MONTHLY, QUARTERLY, SEMI_ANNUALLY
            or YEARLY.
        raise_security_error : bool, default False
            Raise RequestSecurityError for an invalid ticker.
        raise_field_error : bool, default False
            Raise RequestFieldError for an invalid field.
        period_adjustment : str, optional
            ACTUAL, CALENDAR or FISCAL.
        currency : str, optional
            ISO code to convert values into.
        override_option : str, optional
            OVERRIDE_OPTION_CLOSE or OVERRIDE_OPTION_GPA.
        pricing_option : str, optional
            PRICING_OPTION_PRICE or PRICING_OPTION_YIELD.
        non_trading_day_fill_option : str, optional
            NON_TRADING_WEEKDAYS, ALL_CALENDAR_DAYS or ACTIVE_DAYS_ONLY.
        non_trading_day_fill_method : str, optional
            PREVIOUS_VALUE, NIL_VALUE or NEXTDAY_VALUE.
        max_data_points : int, optional
            Cap on points per security, counted back from end.
        adjustment_normal, adjustment_abnormal, adjustment_split : bool, optional
            Bloomberg pricing adjustment flags. False is sent; None is not.
        adjustment_follow_DPDF : bool, optional
            Follow the terminal's DPDF adjustment settings.
        calendar_code_override : str, optional
            Two-letter country ISO code.
        **overrides
            Bloomberg field overrides, field id to value, each sent as its
            str.

        Returns
        -------
        HistoricalDataResponse

        Raises
        ------
        TypeError
            force_string, which blp-net lacks for historical data.
        ValueError
            A date calendar_date cannot read, or an option name outside its
            list.
        RequestSecurityError, RequestFieldError
            As raise_security_error and raise_field_error set.
        ResponseError
            Bloomberg failed the request.
        SessionError
            The session failed, or a limit passed (RequestTimeoutError).

        Notes
        -----
        - A falsy string option or a max_data_points of 0 is not sent, as
          in the Python blp.
        """
        reject_gaps(overrides, {'force_string'}, 'get_historical')
        zone = Timezone(timezone)
        fields = list(to_string_list(flds))
        request = HistoricalDataRequest(
            to_string_list(tickers),
            to_string_list(fields),
            to_date_only(start),
            to_date_only(end))
        request.TimeZone = TimeZoneInfo.FindSystemTimeZoneById(timezone)
        request.Period = to_enum(Periodicity, period or 'DAILY')
        if period_adjustment:
            request.PeriodAdjustment = to_enum(PeriodicityAdjustment, period_adjustment)
        if currency:
            request.Currency = currency
        if override_option:
            request.OverrideOption = to_enum(
                OverrideOption,
                override_option,
                'OVERRIDE_OPTION_')
        if pricing_option:
            request.PricingOption = to_enum(
                PricingOption,
                pricing_option,
                'PRICING_OPTION_')
        if non_trading_day_fill_option:
            request.NonTradingDayFillOption = to_enum(
                NonTradingDayFillOption,
                non_trading_day_fill_option)
        if non_trading_day_fill_method:
            request.NonTradingDayFillMethod = to_enum(
                NonTradingDayFillMethod,
                non_trading_day_fill_method)
        if max_data_points:
            request.MaxDataPoints = int(max_data_points)
        if adjustment_normal is not None:
            request.AdjustmentNormal = bool(adjustment_normal)
        if adjustment_abnormal is not None:
            request.AdjustmentAbnormal = bool(adjustment_abnormal)
        if adjustment_split is not None:
            request.AdjustmentSplit = bool(adjustment_split)
        if adjustment_follow_DPDF is not None:
            request.AdjustmentFollowDpdf = bool(adjustment_follow_DPDF)
        if calendar_code_override:
            request.CalendarCodeOverride = calendar_code_override
        request.RaiseSecurityError = raise_security_error
        request.RaiseFieldError = raise_field_error
        request.Overrides = to_overrides(overrides)
        self._apply_limits(request)
        response = wait_for(
            lambda token: self.client.GetHistoricalDataAsync(request, token))
        return HistoricalDataResponse(response, fields, zone)

    def get_intraday_tick(
        self,
        ticker: str,
        events: str | list[str] | None = None,
        start: datetime.date | str | None = None,
        end: datetime.date | str | None = None,
        timezone: str = LCL.name,
        include_condition_codes: bool | None = None,
        *,
        include_broker_codes: bool | None = None,
        include_equity_ref_price: bool | None = None,
        include_action_codes: bool | None = None,
        include_indicator_codes: bool | None = None,
        include_trade_date: bool | None = None,
        include_trade_time: bool | None = None,
        include_rsp_codes: bool | None = None,
        include_bic_mic_codes: bool | None = None) -> IntradayResponse:
        """Every tick of one security in a time range.

        Parameters
        ----------
        ticker : str
            Bloomberg security identifier.
        events : str or list[str], optional
            Bloomberg event types, such as 'TRADE' or 'BID_BEST'. None sends
            TRADE, ASK and BID.
        start : datetime.date, str or None
            First instant, inclusive, as utc_instant reads it. None, NaN or
            NaT means one day before end.
        end : datetime.date, str or None
            Last instant, inclusive, read as start is. None, NaN or NaT
            means now.
        timezone : str
            IANA zone for date-time and time values in the response. The
            range ignores it.
        include_condition_codes, include_broker_codes,
        include_equity_ref_price, include_action_codes,
        include_indicator_codes, include_trade_date, include_trade_time,
        include_rsp_codes, include_bic_mic_codes : bool, optional
            Add that column to each tick. None or False adds none.

        Returns
        -------
        IntradayResponse
            Columns time, type, value and size, then any added column.

        Raises
        ------
        TypeError
            Any other Python blp flag, which blp-net lacks.
        ValueError
            A range bound utc_instant cannot read, or an event type outside
            IntradayEventType.
        ResponseError
            Bloomberg failed the request.
        SessionError
            The session failed, or a limit passed (RequestTimeoutError).
        """
        zone = Timezone(timezone)
        start_offset, end_offset = intraday_range(start, end)
        request = IntradayTickRequest(ticker, start_offset, end_offset)
        if events is not None:
            event_types = List[IntradayEventType]()
            for event in [events] if isinstance(events, str) else events:
                event_types.Add(to_enum(IntradayEventType, event))
            request.Events = event_types
        request.IncludeConditionCodes = bool(include_condition_codes)
        request.IncludeBrokerCodes = bool(include_broker_codes)
        request.IncludeEquityRefPrice = bool(include_equity_ref_price)
        request.IncludeActionCodes = bool(include_action_codes)
        request.IncludeIndicatorCodes = bool(include_indicator_codes)
        request.IncludeTradeDate = bool(include_trade_date)
        request.IncludeTradeTime = bool(include_trade_time)
        request.IncludeRpsCodes = bool(include_rsp_codes)
        request.IncludeBicMicCodes = bool(include_bic_mic_codes)
        request.TimeZone = TimeZoneInfo.FindSystemTimeZoneById(timezone)
        self._apply_limits(request)
        rows = wait_for(lambda token: self.client.GetIntradayTicksAsync(request, token))
        return IntradayResponse(rows, zone)

    def get_intraday_bar(
        self,
        ticker: str,
        event: str = 'TRADE',
        start: datetime.date | str | None = None,
        end: datetime.date | str | None = None,
        timezone: str = LCL.name,
        interval: int | None = None) -> IntradayResponse:
        """Open, high, low and close bars of one security in a time range.

        Parameters
        ----------
        ticker : str
            Bloomberg security identifier.
        event : str, default 'TRADE'
            Bloomberg event type the bars summarize, such as 'BID'.
        start, end : datetime.date, str or None
            As get_intraday_tick reads them.
        timezone : str
            IANA zone for date-time values in the response. The range
            ignores it.
        interval : int, optional
            Bar length in minutes, 1 to 1440. None or 0 sends 1.

        Returns
        -------
        IntradayResponse
            Columns time, open, high, low, close, volume, numEvents and
            value.

        Raises
        ------
        TypeError
            gap_fill_initial_bar, return_eids or an adjustment flag, which
            blp-net lacks.
        ValueError
            A range bound utc_instant cannot read, or an event type outside
            IntradayEventType.
        ResponseError
            Bloomberg failed the request.
        SessionError
            The session failed, or a limit passed (RequestTimeoutError).
        """
        zone = Timezone(timezone)
        start_offset, end_offset = intraday_range(start, end)
        request = IntradayBarRequest(ticker, start_offset, end_offset)
        request.Event = to_enum(IntradayEventType, event)
        if interval:
            request.Interval = int(interval)
        request.TimeZone = TimeZoneInfo.FindSystemTimeZoneById(timezone)
        self._apply_limits(request)
        rows = wait_for(lambda token: self.client.GetIntradayBarsAsync(request, token))
        return IntradayResponse(rows, zone)

    def get_screener(
        self,
        name: str,
        group: str | None = 'General',
        type: str = 'GLOBAL',
        asof: datetime.date | str | None = None,
        *,
        timezone: str = LCL.name) -> EQSResponse:
        """Equivalent of the EQS screen: the securities a screen selects.

        Parameters
        ----------
        name : str
            The screen's name.
        group : str, default 'General'
            The screen's folder. None or '' sends none.
        type : str, default 'GLOBAL'
            GLOBAL for a Bloomberg screen, PRIVATE for a saved one. None
            raises ValueError.
        asof : datetime.date, str or None
            Runs the screen on the data of this date, as calendar_date
            reads it. None or '' runs it on current data.
        timezone : str
            IANA zone for date-time and time values in the response.

        Returns
        -------
        EQSResponse

        Raises
        ------
        TypeError
            language, which blp-net lacks.
        ValueError
            A type other than GLOBAL or PRIVATE, or a date calendar_date
            cannot read.
        ResponseError
            Bloomberg failed the request.
        SessionError
            The session failed, or a limit passed (RequestTimeoutError).
        """
        zone = Timezone(timezone)
        request = EqsRequest(name)
        request.Group = group
        request.ScreenType = to_enum(EqsScreenType, str(type))
        request.AsOf = to_date_only(asof or None)
        request.TimeZone = TimeZoneInfo.FindSystemTimeZoneById(timezone)
        self._apply_limits(request)
        response = wait_for(lambda token: self.client.GetScreenerAsync(request, token))
        return EQSResponse(response, zone)

    def bql(
        self,
        expression: str | None = None,
        universe: str | None = None,
        fields: str | None = None,
        raise_security_error: bool = False) -> BQLResponse:
        """Equivalent of the Excel BQL() function.

        Parameters
        ----------
        expression : str, optional
            The whole query, such as "get(px_last) for(['IBM US Equity'])".
        universe : str, optional
            The for() clause, used with fields when expression is None.
        fields : str, optional
            The get() clause, used with universe.
        raise_security_error : bool, default False
            Raise RequestSecurityError when Bloomberg reports an exception.

        Returns
        -------
        BQLResponse

        Raises
        ------
        ValueError
            Neither expression nor both universe and fields.
        RequestSecurityError
            As raise_security_error sets. The text joins the messages.
        ResponseError
            Bloomberg failed the request.
        SessionError
            The session failed, or a limit passed (RequestTimeoutError).
        """
        if not expression:
            if not universe or not fields:
                raise ValueError("Either 'expression' or both 'universe' and 'fields' must be provided")
            expression = f'get({fields}) for({universe})'
        request = BqlRequest(expression)
        self._apply_limits(request)
        response = wait_for(lambda token: self.client.GetBqlAsync(request, token))
        bql_response = BQLResponse(response)
        if raise_security_error and bql_response.request.security_errors:
            raise RequestSecurityError(''.join(bql_response.request.security_errors))
        return bql_response

    def _apply_limits(self, request: Any) -> None:
        """Set the request's IdleTimeout and TotalTimeout from idle_seconds
        and total_seconds as they stand now.
        """
        request.IdleTimeout = TimeSpan.FromSeconds(self.idle_seconds)
        request.TotalTimeout = None
        if self.total_seconds is not None:
            request.TotalTimeout = TimeSpan.FromSeconds(self.total_seconds)


PYTHON_LEVEL_BY_LOG_LEVEL = {
    LogLevel.Trace: logging.DEBUG,
    LogLevel.Debug: logging.DEBUG,
    LogLevel.Information: logging.INFO,
    LogLevel.Warning: logging.WARNING,
    LogLevel.Error: logging.ERROR,
    LogLevel.Critical: logging.CRITICAL,
    }


def log_line(level: LogLevel, line: str, error: System.Exception | None) -> None:
    """Write one blp-net log line to this module's logger, with the text of
    the exception it carries on the lines after it.
    """
    text = line if error is None else f'{line}\n{error}'
    logger.log(PYTHON_LEVEL_BY_LOG_LEVEL[level], text)


def utc_or_none(value: DateTimeOffset | None) -> DateTime | None:
    """DateTime in UTC of a blp-net instant, None for null.
    """
    return None if value is None else to_python(value, UTC)


@dataclass
class TopicState:
    """One topic's state, as Subscription.status() copies it.

    Attributes
    ----------
    topic : str
    status : str
        'pending' until Bloomberg answers the subscribe, then 'subscribed',
        or 'failed' on a failure or on a termination remove() did not cause.
    reason : str or None
        Bloomberg's description, for a failed topic.
    subscribed_at, first_data_at, last_tick_at : DateTime or None
        UTC times of SubscriptionStarted, the first tick and the latest tick.
    ticks_last_minute : int
        Ticks received in the minute before the status() call.
    """

    topic: str
    status: str
    reason: str | None
    subscribed_at: DateTime | None
    first_data_at: DateTime | None
    last_tick_at: DateTime | None
    ticks_last_minute: int


@dataclass
class SubscriptionStatus:
    """A consistent copy of a Subscription's state.

    Attributes
    ----------
    failure : str or None
        Why the subscription can no longer deliver data, or None while it
        can: 'session terminated' outside stop(), 'all N topics failed:
        reason', or 'no subscription data within 60 s' of the first add().
        The owner replaces a failed subscription.
    connected : bool
        False between SessionConnectionDown and SessionConnectionUp.
    error_cnt : int
        Exceptions caught from parsing a tick, from on_tick, or from
        stopping.
    last_error : str or None
        'topic: type: message' of the latest one. An exception on_tick
        raises has the type PythonException.
    topics : dict[str, TopicState]
        Held topics, in add order.
    """

    failure: str | None
    connected: bool
    error_cnt: int
    last_error: str | None
    topics: dict[str, TopicState]


class Subscription:
    """Live market data on its own session on localhost:8194 with OS logon.

    Parameters
    ----------
    fields : str or list[str]
        Fields whose changes raise a tick, such as ['BID', 'ASK'].
    on_tick : Callable[[str, dict[str, Any]], None]
        Called with (topic, {field: value}) for each tick, on Bloomberg's
        dispatch thread, with no lock held, so it may call add, remove,
        status or stop. Read at each tick. A row holds the fields present
        in the message, a date-time or time in this host's zone, and NaN
        for a null. The subscription catches and counts what it raises.
    interval : float, default 0
        Seconds between conflated updates, sent to the tenth. None or 0
        leaves updates unconflated. Fixed at construction.
    application_identity_key : str, optional
        Bloomberg application identity key.

    Raises
    ------
    SessionCreateError
        The session failed to start.
    SessionStartError
        //blp/mktdata failed to open.

    Notes
    -----
    - The Python blp's host, port, auth, assumed_timezone,
      desired_timezone and time_as_datetime have no blp-net counterpart,
      so passing one raises TypeError.
    - Leaving a with block stops the session. An exit hook stops any
      session still running.
    """

    def __init__(
        self,
        fields: str | list[str],
        on_tick: Callable[[str, dict[str, Any]], None],
        interval: float = 0,
        *,
        application_identity_key: str | None = None) -> None:
        self.on_tick = on_tick
        options = SubscriptionOptions()
        options.ApplicationIdentityKey = application_identity_key
        options.Interval = TimeSpan.FromSeconds(interval or 0)
        write = Action[LogLevel, String, System.Exception](log_line)
        options.Logger = CallbackLogger(write)
        callback = Action[String, IReadOnlyDictionary[String, Object]](self._dispatch)
        with translated_errors():
            self.subscription = NetSubscription.Start(
                to_string_list(fields),
                callback,
                options)
        live_clients.add(self.subscription)

    def __enter__(self) -> Self:
        return self

    def __exit__(
        self,
        exc_type: type | None,
        exc_value: BaseException | None,
        tb: Any) -> None:
        self.stop()

    def _dispatch(self, topic: str, row: Any) -> None:
        """Hand on_tick the topic and its row, each value converted.
        """
        self.on_tick(topic, {pair.Key: to_python(pair.Value, LCL) for pair in row})

    def add(self, topics: str | list[str]) -> None:
        """Subscribe topics, such as 'IBM US Equity'.

        Parameters
        ----------
        topics : str or list[str]
            A held topic is skipped, except a failed one, which is
            subscribed again.

        Raises
        ------
        SessionNotAvailableError
            The session is not started. No topic of the call is held.
        SessionError
            stop() has begun.
        System.ArgumentException
            Bloomberg rejects a topic, such as ''. No topic of the call is
            held.
        """
        with translated_errors():
            self.subscription.Add(to_string_list(topics))

    def remove(self, topics: str | list[str]) -> None:
        """Unsubscribe topics. A topic not held is skipped.
        """
        with translated_errors():
            self.subscription.Remove(to_string_list(topics))

    def status(self) -> SubscriptionStatus:
        """A consistent copy of the state, as of now.
        """
        net_status = self.subscription.GetStatus()
        topics = {
            pair.Key: TopicState(
                pair.Value.Topic,
                str(pair.Value.Status).lower(),
                pair.Value.Reason,
                utc_or_none(pair.Value.SubscribedAt),
                utc_or_none(pair.Value.FirstDataAt),
                utc_or_none(pair.Value.LastTickAt),
                pair.Value.TicksLastMinute)
            for pair in net_status.Topics
            }
        return SubscriptionStatus(
            net_status.Failure,
            net_status.Connected,
            net_status.ErrorCount,
            net_status.LastError,
            topics)

    def stop(self, timeout: float = 10.0) -> bool:
        """Unsubscribe every topic and release the session.

        Parameters
        ----------
        timeout : float or None, default 10.0
            Longest wait, in seconds, for the session to terminate. None
            waits without limit.

        Returns
        -------
        bool
            True when the session is released. False when the wait timed
            out, or when called from on_tick, which begins the stop and
            returns at once. The exit hook then stops the session again.

        Raises
        ------
        System.ArgumentOutOfRangeException
            timeout is negative. The stop has not begun.
        """
        wait = Timeout.InfiniteTimeSpan
        if timeout is not None:
            wait = TimeSpan.FromSeconds(timeout)
        released = self.subscription.Stop(wait)
        if released:
            live_clients.discard(self.subscription)
        return released
