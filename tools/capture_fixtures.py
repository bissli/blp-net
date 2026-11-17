"""Capture real Bloomberg responses as test fixtures for blp-net.

Runs on a host with a logged-in Bloomberg terminal and the Python blp
installed (blpapi, opendate, pandas, blp). Each case sends one request
twice: once on a raw blpapi session, which records every event as
Bloomberg sent it, and once through the Python blp, whose parsed output is
the expected value for the C# port.

    python capture_fixtures.py

The run takes a few minutes and writes everything to blp-fixtures/ under
the current directory. Zip that folder and hand it back.

Notes
-----
- Run it on a weekday during US market hours, so the subscription cases
  receive live ticks.
- The output holds manifest.json, one <service>.schema.xml per service
  and one cases/<name>.json per case. A case that fails records its
  error and the run goes on, so a failure is a fixture too.
- --day picks the intraday session. The default is the last weekday
  before today, since Bloomberg keeps tick history for a limited span.
- --eqs-screen names a saved EQS screen when the default global screen
  is missing on the terminal.
"""
import argparse
import datetime
import json
import math
import pathlib
import platform
import sys
import time
import traceback
import zoneinfo
from collections.abc import Callable
from typing import Any

import blpapi
import blpapi.test
import pandas as pd
from opendate import LCL, Timezone

from blp import client
from blp.parse import SubscriptionParser

ZONE = 'America/New_York'
HOST = 'localhost'
PORT = 8194
AUTH = 'AuthenticationType=OS_LOGON'
RESPONSE_TIMEOUT_SECONDS = 120
OUT_DIR_NAME = 'blp-fixtures'
EQS_SCREEN = 'Core Capital Ratios'

DATATYPE_NAMES = {
    getattr(blpapi.DataType, name): name
    for name in dir(blpapi.DataType)
    if name.isupper()
    }
CORRELATION_TYPE_NAMES = {
    getattr(blpapi.CorrelationId, name): name
    for name in dir(blpapi.CorrelationId)
    if name.endswith('_TYPE')
    }
EVENT_TYPE_NAMES = {
    getattr(blpapi.Event, name): name
    for name in dir(blpapi.Event)
    if name.isupper() and isinstance(getattr(blpapi.Event, name), int)
    }

SCALAR_FIELDS = [
    'PX_LAST', 'VOLUME', 'NAME', 'CRNCY', 'ID_BB_GLOBAL', 'EQY_SH_OUT',
    'CUR_MKT_CAP', 'LAST_UPDATE_DT', 'TIME', 'LAST_UPDATE', 'DVD_EX_DT',
    'PX_YEST_DT', 'TRADING_DT_REALTIME', 'LAST_TRADE_TIME',
    'LAST_UPDATE_DATE_EOD', 'BID', 'ASK', 'PX_OPEN',
    ]
SCALAR_TICKERS = ['IBM US Equity', 'AAPL US Equity', 'SPX Index']
SUBSCRIBE_TOPICS = ['IBM US Equity', 'SPX Index', 'NOTATICKER US Equity']
SUBSCRIBE_FIELDS = ['LAST_PRICE', 'BID', 'ASK', 'VOLUME', 'TIME', 'LAST_TRADE']


def build_cases(day: datetime.date, eqs_screen: str) -> dict[str, Callable]:
    """Case name to a factory for the Python blp request it sends.

    Parameters
    ----------
    day : datetime.date
        Trading day the intraday cases read. Each window opens at 09:30
        in ZONE, the US equity open.
    eqs_screen : str
        Global or saved EQS screen name.

    Returns
    -------
    dict[str, Callable]
        Each factory returns a fresh request, since a request collects
        its own response.
    """
    session_open = datetime.datetime.combine(
        day, datetime.time(9, 30), zoneinfo.ZoneInfo(ZONE))
    flags = {
        f'include_{name}': True
        for name in [
            'action_codes', 'bic_mic_codes', 'broker_codes', 'condition_codes',
            'equity_ref_price', 'indicator_codes', 'rsp_codes', 'trade_date',
            'trade_time',
            ]
        }
    many_tickers = [
        'IBM US Equity', 'AAPL US Equity', 'MSFT US Equity', 'JPM US Equity',
        'XOM US Equity', 'KO US Equity', 'PG US Equity', 'SPX Index',
        ]
    cases = {
        'refdata_scalars': lambda: client.ReferenceDataRequest(
            SCALAR_TICKERS, SCALAR_FIELDS, timezone=ZONE),
        'refdata_scalars_host_zone': lambda: client.ReferenceDataRequest(
            SCALAR_TICKERS, SCALAR_FIELDS),
        'refdata_scalars_force_string': lambda: client.ReferenceDataRequest(
            SCALAR_TICKERS, SCALAR_FIELDS, timezone=ZONE, force_string=True),
        'refdata_scalars_time_as_datetime': lambda: client.ReferenceDataRequest(
            SCALAR_TICKERS, SCALAR_FIELDS, timezone=ZONE, time_as_datetime=True),
        'refdata_bulk': lambda: client.ReferenceDataRequest(
            ['IBM US Equity', 'INDU Index'], ['DVD_HIST_ALL', 'INDX_MEMBERS'],
            timezone=ZONE),
        'refdata_bulk_force_string': lambda: client.ReferenceDataRequest(
            ['IBM US Equity', 'INDU Index'], ['DVD_HIST_ALL', 'INDX_MEMBERS'],
            timezone=ZONE, force_string=True),
        'refdata_errors': lambda: client.ReferenceDataRequest(
            ['IBM US Equity', 'NOTATICKER US Equity'], ['PX_LAST', 'NOT_A_FIELD'],
            timezone=ZONE),
        'refdata_override': lambda: client.ReferenceDataRequest(
            ['IBM US Equity'], ['BEST_EPS'], timezone=ZONE,
            BEST_FPERIOD_OVERRIDE='1BF'),
        'hist_daily': lambda: client.HistoricalDataRequest(
            ['IBM US Equity', 'SPX Index'], ['PX_LAST', 'VOLUME'],
            start='2024-01-02', end='2024-03-29', timezone=ZONE),
        'hist_weekly_errors': lambda: client.HistoricalDataRequest(
            ['IBM US Equity', 'NOTATICKER US Equity'], ['PX_LAST', 'NOT_A_FIELD'],
            start='2024-01-02', end='2024-03-29', timezone=ZONE, period='WEEKLY'),
        'hist_options': lambda: client.HistoricalDataRequest(
            ['IBM US Equity'], ['PX_LAST'], start='2024-03-01', end='2024-03-31',
            timezone=ZONE, currency='EUR',
            non_trading_day_fill_option='ALL_CALENDAR_DAYS',
            non_trading_day_fill_method='NIL_VALUE', max_data_points=5),
        'hist_pricing_options': lambda: client.HistoricalDataRequest(
            ['IBM US Equity'], ['PX_LAST', 'BEST_EPS'], start='2023-01-01',
            end='2024-12-31', timezone=ZONE, period='QUARTERLY',
            period_adjustment='CALENDAR', override_option='OVERRIDE_OPTION_GPA',
            pricing_option='PRICING_OPTION_PRICE', BEST_FPERIOD_OVERRIDE='1BF'),
        'hist_partial': lambda: client.HistoricalDataRequest(
            many_tickers, ['PX_LAST'], start='2015-01-02', end='2024-12-31',
            timezone=ZONE),
        'intraday_ticks': lambda: client.IntradayTickRequest(
            'IBM US Equity', start=session_open,
            end=session_open + datetime.timedelta(minutes=10), timezone=ZONE,
            events=['BID', 'ASK', 'TRADE'], **flags),
        'intraday_ticks_plain': lambda: client.IntradayTickRequest(
            'IBM US Equity', start=session_open,
            end=session_open + datetime.timedelta(minutes=2), timezone=ZONE,
            events=['TRADE', 'ASK', 'BID']),
        'intraday_bars': lambda: client.IntradayBarRequest(
            'IBM US Equity', start=session_open,
            end=session_open + datetime.timedelta(hours=6, minutes=30),
            timezone=ZONE, event='TRADE', interval=5),
        'intraday_bars_default_interval': lambda: client.IntradayBarRequest(
            'IBM US Equity', start=session_open,
            end=session_open + datetime.timedelta(minutes=30), timezone=ZONE,
            event='TRADE'),
        'bql': lambda: client.BQLRequest(
            expression="get(px_last) for(['IBM US Equity', 'AAPL US Equity'])"),
        'bql_dates': lambda: client.BQLRequest(
            expression=(
                'get(px_last(dates=range(-5d, 0d)), name) '
                "for(['IBM US Equity', 'AAPL US Equity'])")),
        'bql_errors': lambda: client.BQLRequest(
            expression="get(not_a_field) for(['IBM US Equity', 'NOTATICKER US Equity'])"),
        'eqs': lambda: client.EQSRequest(eqs_screen, timezone=ZONE),
        'eqs_unknown_screen': lambda: client.EQSRequest('NOT A SCREEN', timezone=ZONE),
        }
    return cases


def element_tree(element: blpapi.Element) -> dict[str, Any]:
    """Element and its children, with each datatype and value as text.

    Parameters
    ----------
    element : blpapi.Element
        Any element.

    Returns
    -------
    dict[str, Any]
        name, datatype, isArray and isNull, then 'values' for an array,
        'elements' for a sequence or the one chosen element of a choice,
        or 'text' for a scalar. A
        date or time keeps its getValueAsString text, which shows the
        parts Bloomberg set.
    """
    datatype = element.datatype()
    node = {
        'name': str(element.name()),
        'datatype': DATATYPE_NAMES.get(datatype, str(datatype)),
        'isArray': element.isArray(),
        'isNull': element.isNull(),
        }
    is_complex = datatype in {blpapi.DataType.SEQUENCE, blpapi.DataType.CHOICE}
    if element.isArray():
        node['values'] = [
            element_tree(element.getValueAsElement(index)) if is_complex
            else element.getValueAsString(index)
            for index in range(element.numValues())
            ]
    elif datatype == blpapi.DataType.CHOICE and element.isNull():
        node['elements'] = []
    elif datatype == blpapi.DataType.CHOICE:
        node['elements'] = [element_tree(element.getChoice())]
    elif is_complex:
        node['elements'] = [element_tree(child) for child in element.elements()]
    elif not element.isNull():
        node['text'] = element.getValueAsString()
    return node


def correlation_records(message: blpapi.Message) -> list[dict[str, str]]:
    """Each correlation id of message as its type name and value text.

    Parameters
    ----------
    message : blpapi.Message
        Any message.

    Returns
    -------
    list[dict[str, str]]
        {'type': 'INT_TYPE', 'value': '4'} per id. The type tells a
        request's own id from an AUTOGEN_TYPE id with the same value.
    """
    return [
        {
            'type': CORRELATION_TYPE_NAMES.get(cid.type(), str(cid.type())),
            'value': str(cid.value()),
            }
        for cid in message.correlationIds()
        ]


def event_record(event: blpapi.Event) -> dict[str, Any]:
    """One event with every message, as element_tree and toJson give them.
    """
    messages = [{
            'messageType': str(message.messageType()),
            'correlationIds': correlation_records(message),
            'json': message.toJson(),
            'tree': element_tree(message.asElement()),
            } for message in event]
    return {
        'eventType': EVENT_TYPE_NAMES.get(event.eventType(), str(event.eventType())),
        'receivedAtUtc': datetime.datetime.now(datetime.UTC).isoformat(),
        'messages': messages,
        }


def encoded(value: Any) -> Any:
    """Value as JSON with a type tag, so a reader can rebuild it exactly.

    Parameters
    ----------
    value : Any
        A Python blp output: a scalar, a date or time, a DataFrame, or a
        dict or list of these.

    Returns
    -------
    Any
        {'t': tag, 'v': value}. Tags: null, nan, nat, bool, int, float
        (repr text), str, datetime (isoformat with offset), date, time,
        frame (columns, index, rows), dict, list, repr for anything else.
    """
    if value is None:
        return {'t': 'null'}
    if value is pd.NaT:
        return {'t': 'nat'}
    if isinstance(value, pd.DataFrame):
        return {
            't': 'frame',
            'columns': [str(column) for column in value.columns],
            'index': [encoded(label) for label in value.index],
            'rows': [
                [encoded(cell) for cell in row]
                for row in value.itertuples(index=False)
                ],
            }
    if isinstance(value, dict):
        items = {str(key): encoded(item) for key, item in value.items()}
        return {'t': 'dict', 'v': items}
    if isinstance(value, list | tuple):
        return {'t': 'list', 'v': [encoded(item) for item in value]}
    if hasattr(value, 'item') and not isinstance(value, str):
        value = value.item()
    if isinstance(value, bool):
        return {'t': 'bool', 'v': value}
    if isinstance(value, int):
        return {'t': 'int', 'v': value}
    if isinstance(value, float):
        return {'t': 'nan'} if math.isnan(value) else {'t': 'float', 'v': repr(value)}
    if isinstance(value, str):
        return {'t': 'str', 'v': value}
    if isinstance(value, datetime.datetime):
        return {'t': 'datetime', 'v': value.isoformat(), 'tz': str(value.tzinfo)}
    if isinstance(value, datetime.date):
        return {'t': 'date', 'v': value.isoformat()}
    if isinstance(value, datetime.time):
        return {'t': 'time', 'v': value.isoformat(), 'tz': str(value.tzinfo)}
    return {'t': 'repr', 'v': repr(value)}


def raw_events(
    session: blpapi.Session,
    request: client.BaseRequest,
    correlation_id: int) -> tuple[str, list[dict[str, Any]]]:
    """Send request on session and record its events up to the final one.

    Parameters
    ----------
    session : blpapi.Session
        A started session without an event handler.
    request : client.BaseRequest
        A Python blp request, which builds the blpapi request it sends.
    correlation_id : int
        Unique within the session.

    Returns
    -------
    tuple[str, list[dict[str, Any]]]
        The blpapi request as text, and its events in arrival order.

    Raises
    ------
    TimeoutError
        No RESPONSE arrived within RESPONSE_TIMEOUT_SECONDS.
    """
    if not session.openService(request.service_name):
        raise RuntimeError(f'Failed to open {request.service_name}')
    blpapi_request = request.create_request(session.getService(request.service_name))
    cid = blpapi.CorrelationId(correlation_id)
    session.sendRequest(blpapi_request, correlationId=cid)
    events = []
    deadline = time.monotonic() + RESPONSE_TIMEOUT_SECONDS
    while time.monotonic() < deadline:
        event = session.nextEvent(500)
        if event.eventType() == blpapi.Event.TIMEOUT:
            continue
        events.append(event_record(event))
        is_ours = any(cid in message.correlationIds() for message in event)
        if is_ours and event.eventType() in {
                blpapi.Event.RESPONSE, blpapi.Event.REQUEST_STATUS}:
            return str(blpapi_request), events
    # A late event of an abandoned request would land in the next case.
    session.cancel(cid)
    raise TimeoutError(f'No final response in {RESPONSE_TIMEOUT_SECONDS}s')


def capture_subscription(
    session: blpapi.Session,
    seconds: float,
    options: str,
    first_correlation_id: int) -> dict[str, Any]:
    """Subscribe SUBSCRIBE_TOPICS for seconds and record every event.

    Parameters
    ----------
    session : blpapi.Session
        A started session without an event handler.
    seconds : float
        How long to collect ticks before unsubscribing.
    options : str
        Subscription options, such as 'interval=5.0'. Empty for none.
    first_correlation_id : int
        Correlation id of the first topic. The rest follow in order, and
        none may repeat an earlier capture's ids.

    Returns
    -------
    dict[str, Any]
        'events', and 'python': for each data message, the SubscriptionParser
        value of each field present, with the host's UTC time at parse.
    """
    if not session.openService('//blp/mktdata'):
        raise RuntimeError('Failed to open //blp/mktdata')
    subscriptions = blpapi.SubscriptionList()
    for index, topic in enumerate(SUBSCRIBE_TOPICS):
        cid = blpapi.CorrelationId(first_correlation_id + index)
        subscriptions.add(topic, SUBSCRIBE_FIELDS, options, cid)
    session.subscribe(subscriptions)
    parser = SubscriptionParser(assumed_timezone=LCL, desired_timezone=Timezone(ZONE))
    events = []
    parsed = []
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        event = session.nextEvent(500)
        if event.eventType() == blpapi.Event.TIMEOUT:
            continue
        events.append(event_record(event))
        if event.eventType() != blpapi.Event.SUBSCRIPTION_DATA:
            continue
        for message in event:
            element = message.asElement()
            parsed.append({
                'parsedAtUtc': datetime.datetime.now(datetime.UTC).isoformat(),
                'correlationIds': correlation_records(message),
                'values': {
                    field: encoded(parser.get_subelement_value(element, field))
                    for field in SUBSCRIBE_FIELDS
                    if element.hasElement(field)
                    },
                })
    session.unsubscribe(subscriptions)
    stop_at = time.monotonic() + 5
    while time.monotonic() < stop_at:
        event = session.nextEvent(500)
        if event.eventType() != blpapi.Event.TIMEOUT:
            events.append(event_record(event))
    return {
        'topics': SUBSCRIBE_TOPICS,
        'fields': SUBSCRIBE_FIELDS,
        'options': options,
        'events': events,
        'python': parsed,
        }


def main() -> int:
    """Run every case and write the fixtures.

    Returns
    -------
    int
        Count of captures whose raw blpapi side failed. A case whose
        Python blp call raised on a captured response is no failure: the
        raise is its expected value.
    """
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument(
        '--out-dir', type=pathlib.Path, default=pathlib.Path.cwd() / OUT_DIR_NAME)
    parser.add_argument('--day', type=datetime.date.fromisoformat)
    parser.add_argument('--eqs-screen', default=EQS_SCREEN)
    parser.add_argument('--subscribe-seconds', type=float, default=60)
    args = parser.parse_args()
    day = args.day
    if day is None:
        day = datetime.datetime.now(datetime.UTC).date() - datetime.timedelta(days=1)
        while day.weekday() >= 5:
            day -= datetime.timedelta(days=1)
    cases_dir = args.out_dir / 'cases'
    cases_dir.mkdir(parents=True, exist_ok=True)

    options = blpapi.SessionOptions()
    options.setServerHost(HOST)
    options.setServerPort(PORT)
    options.setAuthenticationOptions(AUTH)
    session = blpapi.Session(options)
    if not session.start():
        raise RuntimeError('Failed to start the Bloomberg session')
    startup_events = []
    stop_at = time.monotonic() + 3
    while time.monotonic() < stop_at:
        event = session.nextEvent(500)
        if event.eventType() != blpapi.Event.TIMEOUT:
            startup_events.append(event_record(event))

    failed_cnt = 0
    blp = client.Blp(host=HOST, port=PORT, auth=AUTH)
    cases = build_cases(day, args.eqs_screen)
    for correlation_id, (name, make_request) in enumerate(cases.items(), start=1):
        record = {'case': name}
        try:
            record['request'], record['events'] = raw_events(
                session, make_request(), correlation_id)
        except Exception:
            failed_cnt += 1
            record['rawError'] = traceback.format_exc()
            print(f'{name}: FAILED')
        try:
            request = make_request()
            response = blp.execute(request)
            try:
                record['python'] = encoded(response.as_dict())
            except NotImplementedError:
                record['python'] = encoded(response.as_dataframe())
            record['pythonSecurityErrors'] = [
                str(error) for error in request.security_errors
                ]
            record['pythonFieldErrors'] = [str(error) for error in request.field_errors]
        except Exception:
            record['pythonError'] = traceback.format_exc()
        (cases_dir / f'{name}.json').write_text(json.dumps(record, indent=1))
        if 'rawError' not in record:
            outcome = 'ok, Python blp raised' if 'pythonError' in record else 'ok'
            print(f'{name}: {outcome}')

    for service_name in ['//blp/refdata', '//blp/bqlsvc', '//blp/mktdata']:
        try:
            session.openService(service_name)
            schema = blpapi.test.serializeService(session.getService(service_name))
            schema_name = f'{service_name.removeprefix("//blp/")}.schema.xml'
            (args.out_dir / schema_name).write_text(schema)
        except Exception:
            failed_cnt += 1
            print(f'{service_name} schema: FAILED\n{traceback.format_exc()}')

    subscription_cases = [
        ('subscription', '', 1000),
        ('subscription_interval', 'interval=5.0', 2000),
        ]
    for name, options, first_correlation_id in subscription_cases:
        try:
            subscription = capture_subscription(
                session, args.subscribe_seconds, options, first_correlation_id)
        except Exception:
            failed_cnt += 1
            subscription = {'error': traceback.format_exc()}
        (cases_dir / f'{name}.json').write_text(json.dumps(subscription, indent=1))
        print(f'{name}: {"FAILED" if "error" in subscription else "ok"}')

    session.stop()
    blp.session.cleanup()
    manifest = {
        'capturedAtUtc': datetime.datetime.now(datetime.UTC).isoformat(),
        'intradayDay': day.isoformat(),
        'eqsScreen': args.eqs_screen,
        'responseZone': ZONE,
        'hostLocalZone': LCL.name,
        'blpapiVersion': blpapi.__version__,
        'pythonVersion': sys.version,
        'platform': platform.platform(),
        'startupEvents': startup_events,
        'failedCount': failed_cnt,
        }
    (args.out_dir / 'manifest.json').write_text(json.dumps(manifest, indent=1))
    print(f'{failed_cnt} failures; fixtures in {args.out_dir}')
    return failed_cnt


if __name__ == '__main__':
    sys.exit(1 if main() else 0)
