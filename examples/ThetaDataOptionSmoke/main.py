# region imports
from AlgorithmImports import *
# endregion


class ThetaDataOptionSmoke(QCAlgorithm):
    """Smoke test of the ThetaData connector: SPY, its option chain, and 1 call, on daily end-of-day data.

    It places no order. The log shows the chain size each day, the selected contract, and its prices.
    The filter keeps the run small: calls within 5 strikes of the price that expire within 7 days.
    Write the option files first with the data writer (see LOCAL_LEAN_SETUP.md).
    """

    def initialize(self):
        self.set_start_date(2026, 10, 19)
        self.set_end_date(2026, 10, 23)
        self.set_cash(100_000)

        self._spy = self.add_equity("SPY", Resolution.DAILY).symbol
        option = self.add_option("SPY", Resolution.DAILY)
        option.set_filter(lambda universe: universe.include_weeklys().calls_only().strikes(-5, 5).expiration(0, 7))
        self._canonical = option.symbol
        self._contract = None

    def on_data(self, slice: Slice):
        chain = slice.option_chains.get(self._canonical)
        if chain is None:
            return

        contracts = list(chain)
        self.log(f"{self.time:%Y-%m-%d} chain={len(contracts)} underlying={chain.underlying.price}")

        # The nearest call that lives at least 4 more days, so the trace covers the whole week.
        candidates = [c for c in contracts if (c.expiry.date() - self.time.date()).days >= 4]
        if self._contract is None and candidates:
            nearest = sorted(candidates, key=lambda c: (c.expiry, abs(c.strike - chain.underlying.price)))[0]
            self._contract = nearest.symbol
            self.log(f"selected {self._contract.value} expiry={nearest.expiry:%Y-%m-%d} strike={nearest.strike}")

        contract = chain.contracts.get(self._contract) if self._contract is not None else None
        if contract is not None:
            self.log(f"{self.time:%Y-%m-%d} {contract.symbol.value} bid={contract.bid_price} ask={contract.ask_price} "
                     f"last={contract.last_price} volume={contract.volume}")
