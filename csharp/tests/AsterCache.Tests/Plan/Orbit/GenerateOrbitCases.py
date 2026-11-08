"""Generate fixed ORBIT planner parity cases from the read-only Python reference."""

from __future__ import annotations

import hashlib
import json
import math
import random
import sys
from pathlib import Path


def number(value: float | int | str) -> float:
    if isinstance(value, str):
        return {"NaN": math.nan, "Infinity": math.inf, "-Infinity": -math.inf}[value]
    return float(value)


def envelope() -> dict[str, float | str]:
    return {
        "value_low": 0.8,
        "value_high": 1.25,
        "partial_error": 0.1,
        "calibration_version": "fixture-v1",
    }


def device_envelope() -> dict[str, float | str]:
    return {
        "seek_min": 0.5,
        "seek_max": 1.7,
        "throughput_min": 0.6,
        "throughput_max": 1.8,
        "calibration_version": "fixture-v1",
    }


def world_data(seed: int, block_count: int, episode_count: int, *, confidence: float = 0.82) -> dict:
    blocks = []
    for i in range(block_count):
        blocks.append({
            "key": f"asset-{i:02d}",
            "size": 3 + (seed * 7 + i * 11) % 37,
            "position": ((seed * 13 + i * 29) % 113) + i / 16.0,
            "frequency": ((seed * 5 + i * 17) % 31) / 3.0,
        })

    episodes = []
    for e in range(episode_count):
        required_count = min(block_count, 1 + (seed + e * 7) % min(8, block_count))
        cursor = (seed * 3 + e * 5) % block_count
        required_indices = [(cursor + offset) % block_count for offset in range(required_count)]
        episodes.append({
            "requires": [f"asset-{i:02d}" for i in required_indices],
            "predicted": 0.2 + ((seed * 13 + e * 19) % 71) / 9.0,
            "predicted_partial": ((seed + e * 11) % 11) / 10.0,
        })

    return {
        "blocks": blocks,
        "episodes": episodes,
        "budget": sum(block["size"] for block in blocks) * 3 // 4,
        "timebox": 80.0 + block_count * 4.0,
        "transfer_units_per_ms": 7.0 + seed % 23,
        "seek_ms": (seed % 7) / 2.0,
        "seek_gap": 8.0 + seed % 27,
        "interruption_hazard": (seed % 13) / 1500.0,
        "model_confidence": confidence,
        "disk_idle": True,
        "disk_known": True,
        "other_game_active": False,
        "baseline_read_allowed": True,
        "signature": f"fixture-{seed}",
    }


def base_case(name: str, seed: int = 4, blocks: int = 7, episodes: int = 9) -> dict:
    return {
        "name": name,
        "world": world_data(seed, blocks, episodes),
        "uncertainty": envelope(),
        "device": device_envelope(),
        "width": 8,
        "max_expansions": 360,
        "partition_k": 4,
        "candidate_set": True,
    }


def exact_device_envelope() -> dict[str, float | str]:
    return {
        "seek_min": 1.0,
        "seek_max": 1.0,
        "throughput_min": 1.0,
        "throughput_max": 1.0,
        "calibration_version": "fixture-v1",
    }


def single_block_case(name: str, key: str = "asset-00", size: int = 1) -> dict:
    case = base_case(name, 27, 1, 1)
    world = case["world"]
    world["blocks"] = [{"key": key, "size": size, "position": 0.0, "frequency": 1.0}]
    world["episodes"] = [{"requires": [key], "predicted": 1.0, "predicted_partial": 1.0}]
    world["budget"] = size
    world["timebox"] = 2.0
    world["transfer_units_per_ms"] = float(size)
    world["seek_ms"] = 0.0
    world["seek_gap"] = 0.0
    world["interruption_hazard"] = 0.0
    case["uncertainty"] = {
        "value_low": 1.0,
        "value_high": 1.0,
        "partial_error": 0.0,
        "calibration_version": "fixture-v1",
    }
    case["device"] = exact_device_envelope()
    return case


def tied_orbit_case(name: str, *, confidence: float = 0.82, unicode_keys: bool = False) -> dict:
    case = base_case(name, 28, 3, 2)
    first = "\uE000" if unicode_keys else "block-b"
    second = "\U00010000" if unicode_keys else "block-c"
    case["world"]["blocks"] = [
        {"key": "anchor", "size": 1, "position": 0.0, "frequency": 1.0},
        {"key": first, "size": 1, "position": 1.0, "frequency": 1.0},
        {"key": second, "size": 1, "position": 2.0, "frequency": 1.0},
    ]
    case["world"]["episodes"] = [
        {"requires": ["anchor"], "predicted": 20.0, "predicted_partial": 1.0},
        {"requires": [first, second], "predicted": 30.0, "predicted_partial": 0.0},
    ]
    case["world"]["budget"] = 2
    case["world"]["timebox"] = 10.0
    case["world"]["transfer_units_per_ms"] = 1.0
    case["world"]["seek_ms"] = 0.0
    case["world"]["seek_gap"] = 0.0
    case["world"]["interruption_hazard"] = 0.0
    case["world"]["model_confidence"] = confidence
    case["uncertainty"] = {
        "value_low": 1.0,
        "value_high": 1.0,
        "partial_error": 0.0,
        "calibration_version": "fixture-v1",
    }
    case["device"] = exact_device_envelope()
    case["partition_k"] = 1
    case["width"] = 8
    case["max_expansions"] = 480
    return case


def fsum_cases() -> list[dict]:
    vectors = [
        ("reported-cancellation", [0.1, -1e16, 9999999999999998.0]),
        ("cancellation-small-last", [1e16, 1.0, -1e16]),
        ("cancellation-large-first", [1e16, -1e16, 1.0]),
        ("cancellation-wide-exponent", [1e100, 1.0, -1e100]),
    ]
    generator = random.Random(20261108)
    for index in range(256):
        values = [
            math.ldexp(generator.uniform(-1.0, 1.0), generator.randint(-900, 900))
            for _ in range(generator.randint(1, 24))
        ]
        vectors.append((f"random-{index:03d}", values))

    return [
        {"name": name, "values": values, "expected": math.fsum(values)}
        for name, values in vectors
    ]


def required_inputs(root: Path):
    sys.path.insert(0, str(root / "src"))
    sys.path.insert(0, str(root / "prior" / "src"))
    from orbit2_original import Block
    from orbit2_prediction_only import PlanningWorld, PredictedEpisode
    from orbit_robust import DeviceEnvelope, Uncertainty, choose

    return Block, PredictedEpisode, PlanningWorld, Uncertainty, DeviceEnvelope, choose


def python_number(value, convert):
    return convert(value) if isinstance(value, str) else value


def reference_decision(case: dict, types):
    Block, PredictedEpisode, PlanningWorld, Uncertainty, DeviceEnvelope, choose = types
    raw_world = case["world"]
    if raw_world is None:
        world = None
    else:
        blocks = tuple(Block(
            row["key"], row["size"], python_number(row["position"], number),
            python_number(row["frequency"], number)) for row in raw_world["blocks"])
        episodes = tuple(PredictedEpisode(
            tuple(row["requires"]), python_number(row["predicted"], number),
            python_number(row["predicted_partial"], number)) for row in raw_world["episodes"])
        world = PlanningWorld(
            blocks, episodes, raw_world["budget"],
            python_number(raw_world["timebox"], number),
            python_number(raw_world["transfer_units_per_ms"], number),
            python_number(raw_world["seek_ms"], number),
            python_number(raw_world["seek_gap"], number),
            python_number(raw_world["interruption_hazard"], number),
            python_number(raw_world["model_confidence"], number),
            raw_world["disk_idle"], raw_world["disk_known"],
            raw_world["other_game_active"], raw_world["baseline_read_allowed"],
            raw_world["signature"])

    raw_uncertainty = case["uncertainty"]
    uncertainty = None if raw_uncertainty is None else Uncertainty(
        number(raw_uncertainty["value_low"]), number(raw_uncertainty["value_high"]),
        number(raw_uncertainty["partial_error"]), raw_uncertainty["calibration_version"])
    raw_device = case["device"]
    device = None if raw_device is None else DeviceEnvelope(
        number(raw_device["seek_min"]), number(raw_device["seek_max"]),
        number(raw_device["throughput_min"]), number(raw_device["throughput_max"]),
        raw_device["calibration_version"])
    return choose(world, uncertainty=uncertainty, device=device,
                  width=case["width"], max_expansions=case["max_expansions"],
                  partition_k=case["partition_k"], candidate_set=case["candidate_set"])


def cases() -> list[dict]:
    result = [base_case("baseline-without-calibration", 2, 6, 8)]
    result[0]["uncertainty"] = None
    result.append(base_case("partition-k-1", 3, 5, 8))
    result[-1]["partition_k"] = 1
    for k in range(2, 9):
        case = base_case(f"partition-k-{k}", 20 + k, 8, 12)
        case["partition_k"] = k
        result.append(case)

    result.append(base_case("empty-episodes", 8, 5, 0))
    result.append(base_case("single-block", 9, 1, 3))
    result.append(base_case("low-confidence", 11, 8, 9))
    result[-1]["world"]["model_confidence"] = 0.54
    result.append(base_case("policy-only-candidate", 12, 7, 9))
    result[-1]["candidate_set"] = False

    max_case = base_case("upper-bounds", 17, 24, 96)
    max_case["world"]["episodes"] = [
        {
            "requires": [f"asset-{(i * 7 + j * 5) % 24:02d}" for j in range(4)],
            "predicted": 0.5 + (i % 19) / 4.0,
            "predicted_partial": (i % 11) / 10.0,
        }
        for i in range(96)
    ]
    max_case["world"]["budget"] = sum(block["size"] for block in max_case["world"]["blocks"])
    max_case["world"]["timebox"] = 10000.0
    max_case["partition_k"] = 8
    max_case["width"] = 8
    max_case["max_expansions"] = 480
    result.append(max_case)

    for name, flag, value in [
        ("disk-not-idle", "disk_idle", False),
        ("disk-unknown", "disk_known", False),
        ("other-game-active", "other_game_active", True),
        ("baseline-read-forbidden", "baseline_read_allowed", False),
    ]:
        case = base_case(name)
        case["world"][flag] = value
        result.append(case)

    for name, blocks, episodes in [
        ("zero-blocks", [], []),
        ("too-many-blocks", world_data(3, 25, 1)["blocks"], world_data(3, 25, 1)["episodes"]),
    ]:
        case = base_case(name)
        case["world"]["blocks"] = blocks
        case["world"]["episodes"] = episodes
        result.append(case)

    case = base_case("too-many-episodes", 5, 24, 96)
    case["world"]["episodes"].append({"requires": ["asset-00"], "predicted": 1.0, "predicted_partial": 0.5})
    result.append(case)

    case = base_case("too-many-dependencies", 6, 24, 49)
    for episode in case["world"]["episodes"][:48]:
        episode["requires"] = [f"asset-{i:02d}" for i in range(8)]
    case["world"]["episodes"][48]["requires"] = ["asset-00"]
    result.append(case)

    for name, mutation in [
        ("duplicate-block-key", lambda w: w["blocks"].__setitem__(1, dict(w["blocks"][0]))),
        ("unknown-requirement", lambda w: w["episodes"][0].__setitem__("requires", ["missing"])),
        ("duplicate-requirement", lambda w: w["episodes"][0].__setitem__("requires", ["asset-00", "asset-00"])),
        ("nine-requirements", lambda w: w["episodes"][0].__setitem__("requires", [f"asset-{i:02d}" for i in range(9)])),
        ("zero-size", lambda w: w["blocks"][0].__setitem__("size", 0)),
        ("oversized-block", lambda w: w["blocks"][0].__setitem__("size", 1_000_000_000_001)),
        ("nonfinite-position", lambda w: w["blocks"][0].__setitem__("position", "NaN")),
        ("negative-prediction", lambda w: w["episodes"][0].__setitem__("predicted", -1.0)),
        ("partial-over-one", lambda w: w["episodes"][0].__setitem__("predicted_partial", 1.01)),
        ("unsafe-predicted-total", lambda w: w["episodes"].__setitem__(slice(0, 2), [
            {"requires": ["asset-00"], "predicted": 6e299, "predicted_partial": 0.5},
            {"requires": ["asset-01"], "predicted": 6e299, "predicted_partial": 0.5},
        ])),
    ]:
        case = base_case(name)
        mutation(case["world"])
        result.append(case)

    for name, option, value in [
        ("width-zero", "width", 0),
        ("width-over-limit", "width", 9),
        ("expansions-zero", "max_expansions", 0),
        ("expansions-over-limit", "max_expansions", 481),
        ("partition-zero", "partition_k", 0),
        ("partition-over-limit", "partition_k", 9),
    ]:
        case = base_case(name)
        case[option] = value
        result.append(case)

    for name, change in [
        ("blank-uncertainty-version", lambda u: u.__setitem__("calibration_version", "  ")),
        ("uncertainty-low-over-one", lambda u: u.__setitem__("value_low", 1.01)),
        ("uncertainty-high-under-one", lambda u: u.__setitem__("value_high", 0.99)),
        ("uncertainty-partial-over-one", lambda u: u.__setitem__("partial_error", 1.01)),
    ]:
        case = base_case(name)
        change(case["uncertainty"])
        result.append(case)

    case = base_case("missing-device")
    case["device"] = None
    result.append(case)
    for name, change in [
        ("blank-device-version", lambda d: d.__setitem__("calibration_version", " ")),
        ("zero-throughput-min", lambda d: d.__setitem__("throughput_min", 0.0)),
        ("device-seek-range-invalid", lambda d: d.__setitem__("seek_max", 0.9)),
    ]:
        case = base_case(name)
        change(case["device"])
        result.append(case)

    case = base_case("derived-device-overflow", 21, 4, 4)
    case["world"]["transfer_units_per_ms"] = 1.7e308
    case["device"]["throughput_max"] = 10.0
    result.append(case)

    case = tied_orbit_case("eligible-order-tie-codepoints", unicode_keys=True)
    case["max_expansions"] = 12
    result.append(case)
    result.append(tied_orbit_case("confidence-and-calibration-boundaries", confidence=0.55))

    case = base_case("positive-lower-without-proxy-improvement", 29, 5, 7)
    case["world"]["blocks"] = [
        {"key": "k0", "size": 2, "position": 2.0, "frequency": 1.0},
        {"key": "k1", "size": 1, "position": 6.0, "frequency": 2.0},
        {"key": "k2", "size": 1, "position": 12.0, "frequency": 3.0},
        {"key": "k3", "size": 1, "position": 4.0, "frequency": 5.0},
        {"key": "k4", "size": 2, "position": 0.0, "frequency": 4.0},
    ]
    case["world"]["episodes"] = [
        {"requires": ["k4", "k3"], "predicted": 20.0, "predicted_partial": 0.5},
        {"requires": ["k4"], "predicted": 1.0000000000000002e16, "predicted_partial": 1.0},
        {"requires": ["k3"], "predicted": 100000000.0, "predicted_partial": 0.0},
        {"requires": ["k2", "k1", "k4", "k3"], "predicted": 2.0, "predicted_partial": 0.5},
        {"requires": ["k4"], "predicted": 1e16, "predicted_partial": 1.0},
        {"requires": ["k0", "k2"], "predicted": 2.0, "predicted_partial": 0.5},
        {"requires": ["k1"], "predicted": 0.2, "predicted_partial": 0.0},
    ]
    case["world"]["budget"] = 6
    case["world"]["timebox"] = 15.0
    case["world"]["transfer_units_per_ms"] = 2.0
    case["world"]["seek_ms"] = 1.0
    case["world"]["seek_gap"] = 10.0
    case["world"]["interruption_hazard"] = 0.01
    case["uncertainty"] = {
        "value_low": 1.0,
        "value_high": 1.0,
        "partial_error": 0.0,
        "calibration_version": "fixture-v1",
    }
    case["device"] = exact_device_envelope()
    case["partition_k"] = 1
    result.append(case)

    case = single_block_case("equal-partition-edges")
    case["partition_k"] = 8
    result.append(case)

    case = single_block_case("timebox-conservative-boundary")
    case["world"]["timebox"] = 1.0
    result.append(case)

    result.append(single_block_case("key-length-512", key="k" * 512))
    result.append(single_block_case("key-length-513", key="k" * 513))

    case = single_block_case("size-exactly-1e12", size=1_000_000_000_000)
    result.append(case)

    case = single_block_case("finite-derived-cost-near-max-double")
    case["world"]["transfer_units_per_ms"] = sys.float_info.max
    result.append(case)

    case = base_case("null-world")
    case["world"] = None
    result.append(case)

    case = base_case("infinite-episode-value")
    case["world"]["episodes"][0]["predicted"] = "Infinity"
    result.append(case)

    case = base_case("advantage-product-overflow-rejected")
    case["world"]["episodes"][0]["predicted"] = 1.7976931348623157e308
    result.append(case)

    case = base_case("infinite-uncertainty-bound")
    case["uncertainty"]["value_high"] = "Infinity"
    result.append(case)

    case = base_case("infinite-device-bound")
    case["device"]["throughput_max"] = "Infinity"
    result.append(case)

    for seed in range(31, 45):
        case = base_case(f"varied-{seed}", seed, 2 + seed % 12, seed % 17)
        case["partition_k"] = 1 + seed % 8
        case["width"] = 1 + seed % 8
        case["max_expansions"] = 1 + seed * 7 % 480
        result.append(case)

    # Inputs found by sweeping seeds (the reference decides; expected values are never edited): wide = fixture envelope, tight = exact envelope.
    for env, seed, blocks in [
        ("wide", 52, 8), ("wide", 63, 8), ("wide", 68, 8), ("wide", 79, 8), ("wide", 80, 12),
        ("tight", 72, 4), ("tight", 100, 4), ("tight", 55, 6), ("tight", 75, 6),
        ("tight", 56, 8), ("tight", 50, 12), ("tight", 97, 12),
    ]:
        case = base_case(f"adopted-{env}-{seed}-{blocks}", seed, blocks, 2 + seed % 9)
        if env == "tight":
            case["uncertainty"] = {"value_low": 1.0, "value_high": 1.0, "partial_error": 0.0, "calibration_version": "fixture-v1"}
            case["device"] = exact_device_envelope()
            case["partition_k"] = 1
        result.append(case)

    return result


def main() -> None:
    if len(sys.argv) != 3:
        raise SystemExit("usage: python GenerateOrbitCases.py <reference-root> <output-json>")

    reference_root = Path(sys.argv[1]).resolve()
    output_path = Path(sys.argv[2]).resolve()
    # Python 3.12 uses compensated float summation in sum(), which can shift planner values by one ULP.
    if sys.version_info[:2] != (3, 11):
        print(f"Warning: generated with Python {sys.version_info.major}.{sys.version_info.minor}; Python 3.11 is the parity reference.")

    types = required_inputs(reference_root)
    reference_sha256 = hashlib.sha256((reference_root / "src" / "orbit_robust.py").read_bytes()).hexdigest()
    fixture_cases = cases()
    for case in fixture_cases:
        decision = reference_decision(case, types)
        case["expected"] = {
            "selected": decision.selected,
            "reason": decision.reason,
            "plan": {
                "order": list(decision.plan.order),
                "elapsed_ms": decision.plan.elapsed_ms,
                "bytes_units": decision.plan.bytes_units,
                "predicted_proxy": decision.plan.predicted_proxy,
                "source": decision.plan.source,
            },
            "minimum_advantage": decision.minimum_advantage,
            "prediction_advantage": decision.prediction_advantage,
        }

    output_path.parent.mkdir(parents=True, exist_ok=True)
    contents = json.dumps({"python_version": sys.version, "orbit_robust_sha256": reference_sha256, "cases": fixture_cases,
                           "fsum_cases": fsum_cases()}, separators=(",", ":"), allow_nan=False)  # no line breaks, no trailing newline
    output_path.write_bytes(contents.encode("utf-8"))
    chosen = sum(1 for case in fixture_cases if case["expected"]["selected"] == "orbit")
    print(f"Wrote {len(fixture_cases)} planner cases ({chosen} choose orbit) and {len(fsum_cases())} fsum cases to {output_path}; orbit_robust.py sha256 {reference_sha256}")


if __name__ == "__main__":
    main()
