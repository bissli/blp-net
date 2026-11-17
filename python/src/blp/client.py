"""Bloomberg requests through blp-net, returned as pandas and opendate values.
"""
import atexit
import datetime
from collections import defaultdict, namedtuple
from collections.abc import Callable, Iterator
from contextlib import contextmanager
from types import SimpleNamespace
from typing import Any, Self

import clr
import numpy as np
import pandas as pd
import System
from Blp import BlpClient, BlpClientOptions, RequestFieldException
from Blp import RequestSecurityException, RequestTimeoutException
from Blp import ResponseException, SessionCreateException, SessionException
from Blp import SessionNotAvailableException, SessionStartException
from Blp import SessionTerminatedException
from Blp.HistoricalData import HistoricalDataRequest, NonTradingDayFillMethod
from Blp.HistoricalData import NonTradingDayFillOption, OverrideOption
from Blp.HistoricalData import Periodicity, PeriodicityAdjustment
from Blp.HistoricalData import PricingOption
from Blp.ReferenceData import ReferenceDataRequest
from opendate import LCL, UTC, Date, DateTime, Time, Timezone
from System import DateOnly, DateTimeOffset, IAsyncResult, String, TimeOnly
from System import TimeSpan, TimeZoneInfo
from System.Collections.Generic import Dictionary, List
from System.Threading import CancellationToken, CancellationTokenSource
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


live_clients: set = set()


@atexit.register
def dispose_live_clients() -> None:
    """Stop the session of every Blp not yet closed.
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

    def _apply_limits(self, request: Any) -> None:
        """Set the request's IdleTimeout and TotalTimeout from idle_seconds
        and total_seconds as they stand now.
        """
        request.IdleTimeout = TimeSpan.FromSeconds(self.idle_seconds)
        request.TotalTimeout = None
        if self.total_seconds is not None:
            request.TotalTimeout = TimeSpan.FromSeconds(self.total_seconds)
