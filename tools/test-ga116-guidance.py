#!/usr/bin/env python3
"""Deterministic geometry regressions for Guided Arrow v1.1.6.

This is a controller-level model of the minimum-turn envelope and recovery state.
It proves the reported stable pure-pursuit orbit exists in the old policy and that
latched coast/re-entry breaks that orbit without granting extra turn authority.
"""
from __future__ import annotations

import math
from dataclasses import dataclass
from pathlib import Path
from typing import Tuple

Vec2 = Tuple[float, float]
CAPTURE_RADIUS = 0.35


def clamp(value: float, minimum: float, maximum: float) -> float:
    return max(minimum, min(maximum, value))


def norm(v: Vec2) -> float:
    return math.hypot(v[0], v[1])


def normalized(v: Vec2) -> Vec2:
    length = norm(v)
    if length <= 1e-12:
        raise AssertionError("zero-length vector")
    return (v[0] / length, v[1] / length)


def unsigned_angle(a: Vec2, b: Vec2) -> float:
    return math.acos(clamp(a[0] * b[0] + a[1] * b[1], -1.0, 1.0))


def minimum_reachable_distance(radius: float, heading_angle: float) -> float:
    radius = max(0.1, radius)
    heading_angle = clamp(heading_angle, 0.0, math.pi)
    if heading_angle <= math.pi * 0.5:
        return 2.0 * radius * math.sin(heading_angle)
    return 2.0 * radius + radius * (heading_angle - math.pi * 0.5)


def waypoint_reachable(position: Vec2, heading: Vec2, waypoint: Vec2, radius: float, reserve: float) -> bool:
    offset = (waypoint[0] - position[0], waypoint[1] - position[1])
    distance = norm(offset)
    if distance <= CAPTURE_RADIUS:
        return True
    direction = normalized(offset)
    angle = unsigned_angle(heading, direction)
    required = minimum_reachable_distance(radius, angle)
    turn_demand = math.sin(min(angle, math.pi * 0.5))
    demanded_reserve = max(0.0, reserve) * clamp(turn_demand, 0.0, 1.0)
    return distance + 0.001 >= required + demanded_reserve


def rotate_towards(current: Vec2, desired: Vec2, maximum_angle: float) -> Vec2:
    signed = math.atan2(
        current[0] * desired[1] - current[1] * desired[0],
        current[0] * desired[0] + current[1] * desired[1],
    )
    signed = clamp(signed, -maximum_angle, maximum_angle)
    c = math.cos(signed)
    s = math.sin(signed)
    return (current[0] * c - current[1] * s, current[0] * s + current[1] * c)


def point_segment_distance(point: Vec2, start: Vec2, end: Vec2) -> float:
    delta = (end[0] - start[0], end[1] - start[1])
    length_squared = delta[0] * delta[0] + delta[1] * delta[1]
    if length_squared <= 1e-12:
        return norm((point[0] - start[0], point[1] - start[1]))
    t = clamp(
        ((point[0] - start[0]) * delta[0] + (point[1] - start[1]) * delta[1]) / length_squared,
        0.0,
        1.0,
    )
    closest = (start[0] + delta[0] * t, start[1] + delta[1] * t)
    return norm((point[0] - closest[0], point[1] - closest[1]))


@dataclass
class SimulationResult:
    hit: bool
    minimum_distance: float
    recovery_entries: int
    recovery_exits: int
    elapsed: float


def simulate(target: Vec2, *, fixed: bool, dt: float = 0.02, duration: float = 8.0) -> SimulationResult:
    position = (0.0, 0.0)
    heading = (1.0, 0.0)
    radius = 24.0
    speed = 70.0
    recovery = False
    entries = 0
    exits = 0
    minimum_distance = float("inf")

    for step in range(int(duration / dt)):
        offset = (target[0] - position[0], target[1] - position[1])
        desired = normalized(offset)
        steer = True

        if fixed:
            ordinary_reserve = max(0.75, radius * 0.08) + speed * dt * 1.5
            recovery_reserve = max(ordinary_reserve, max(2.0, radius * 0.35))
            if recovery:
                if waypoint_reachable(position, heading, target, radius, recovery_reserve):
                    recovery = False
                    exits += 1
                else:
                    steer = False
            elif not waypoint_reachable(position, heading, target, radius, ordinary_reserve):
                recovery = True
                entries += 1
                steer = False

        if steer:
            heading = rotate_towards(heading, desired, (speed / radius) * dt)

        previous = position
        position = (position[0] + heading[0] * speed * dt, position[1] + heading[1] * speed * dt)
        segment_distance = point_segment_distance(target, previous, position)
        minimum_distance = min(minimum_distance, segment_distance)
        if segment_distance <= CAPTURE_RADIUS:
            return SimulationResult(True, minimum_distance, entries, exits, (step + 1) * dt)

    return SimulationResult(False, minimum_distance, entries, exits, duration)


def assert_source_gates(source: Path) -> None:
    text = source.read_text(encoding="utf-8")
    required = (
        "GuidanceRecoveryActive",
        "GuidanceForceDirectIntercept",
        "GetAutoguidanceRecoveryReengageReserve",
        "ShouldForceDirectTerminalIntercept",
        "A decorative profile waypoint may become infeasible",
        "float demandedReserve = Math.Max(0f, reserve) * Clamp(turnDemand, 0f, 1f);",
    )
    for marker in required:
        assert marker in text, f"missing v1.1.6 source marker: {marker}"
    assert "bestFallbackIndex" not in text, "unreachable-target fallback was reintroduced"


def main() -> None:
    source = Path(__file__).resolve().parent.parent / "GuidedArrow" / "Source" / "GuidedArrowBehavior.cs"
    if source.exists():
        assert_source_gates(source)

    assert not waypoint_reachable((0.0, 0.0), (1.0, 0.0), (0.0, 20.0), 24.0, 0.0)
    assert waypoint_reachable((0.0, 0.0), (1.0, 0.0), (2.0, 0.0), 24.0, 20.0)
    assert waypoint_reachable((0.0, 0.0), (1.0, 0.0), (60.0, 0.0), 24.0, 4.0)
    assert not waypoint_reachable((0.0, 0.0), (1.0, 0.0), (20.0, 30.0), 24.0, 4.0)

    old = simulate((0.0, 20.0), fixed=False)
    fixed = simulate((0.0, 20.0), fixed=True)
    assert not old.hit and old.minimum_distance > 10.0, old
    assert fixed.hit and fixed.elapsed < 5.0, fixed
    assert fixed.recovery_entries == 1 and fixed.recovery_exits == 1, fixed

    for target in ((10.0, 10.0), (-10.0, 10.0), (-20.0, 0.0), (20.0, 20.0), (-30.0, 5.0)):
        result = simulate(target, fixed=True)
        assert result.hit and result.elapsed < 6.0, (target, result)
        assert result.recovery_entries <= 1 and result.recovery_exits <= 1, (target, result)

    print("GUIDANCE_GEOMETRY_TESTS=PASS")
    print(f"OLD_ORBIT_MIN_DISTANCE={old.minimum_distance:.6f}")
    print(f"FIXED_INTERCEPT_SECONDS={fixed.elapsed:.6f}")
    print(f"FIXED_RECOVERY_TRANSITIONS={fixed.recovery_entries + fixed.recovery_exits}")


if __name__ == "__main__":
    main()
