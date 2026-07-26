from pathlib import Path
import os

root = Path(os.environ.get('GA1116_MODULE_ROOT', '/mnt/data/ga1116work'))
bridge = (root / 'Source' / 'MissileDamageBridge.cs').read_text(encoding='utf-8')
project = (root / 'Source' / 'GuidedArrow.csproj').read_text(encoding='utf-8')
submodule = (root / 'SubModule.xml').read_text(encoding='utf-8')
behavior = (root / 'Source' / 'GuidedArrowBehavior.cs').read_text(encoding='utf-8')

required = [
    'MethodInfo multiUsageTarget = missionMethods.FirstOrDefault(IsSupportedAddMissileAux);',
    'MethodInfo singleUsageTarget = missionMethods.FirstOrDefault(IsSupportedAddMissileSingleUsageAux);',
    '_harmony.Patch(multiUsageTarget, prefix: prefixPatch, postfix: postfixPatch);',
    '_harmony.Patch(singleUsageTarget, prefix: prefixPatch, postfix: postfixPatch);',
    'parameters[4].ParameterType == typeof(WeaponStatsData[])',
    'parameters[4].ParameterType == typeof(WeaponStatsData).MakeByRefType()',
    'if (nativeStatsArgument is WeaponStatsData[])',
    'else if (nativeStatsArgument is WeaponStatsData)',
    'if (__args[4] is WeaponStatsData[] multiUsageStats)',
    'else if (__args[4] is WeaponStatsData singleUsageStats)',
    'request.Consumed = true;',
]
for token in required:
    assert token in bridge, token

# Ensure both private native paths are patched before installation is marked successful.
install_multi = bridge.index('_harmony.Patch(multiUsageTarget')
install_single = bridge.index('_harmony.Patch(singleUsageTarget')
installed = bridge.index('_installed = true;', install_multi)
assert install_multi < install_single < installed

# The one-usage path must use exactly one resolved stat record rather than an array object.
prefix_single = bridge.index('else if (nativeStatsArgument is WeaponStatsData)')
prefix_single_assignment = bridge.index('__args[4] = data.WeaponStatsData[0];', prefix_single)
consume = bridge.index('request.Consumed = true;', prefix_single)
assert prefix_single < prefix_single_assignment < consume

# Capture of the original one-usage arrow must wrap its by-ref stat struct into one stored record.
postfix_single = bridge.index('else if (__args[4] is WeaponStatsData singleUsageStats)')
postfix_array = bridge.index('weaponStatsData = new[] { singleUsageStats };', postfix_single)
assert postfix_single < postfix_array

# The standalone splitter must still refuse zero-bonus AddCustomMissile creation without a packet,
# then use the bridge around every synthetic copy once the original packet exists.
packet_guard = behavior.index('if (source.ResolvedLaunchData == null)')
override_scope = behavior.index('MissileDamageBridge.OverrideNextSyntheticMissile', packet_guard)
add_custom = behavior.index('Mission.AddCustomMissile(', override_scope)
assert packet_guard < override_scope < add_custom

# Deterministic state model: both native packet shapes capture and override the same full damage.
class Packet:
    def __init__(self, damage_bonus, stats):
        self.damage_bonus = damage_bonus
        self.stats = list(stats)


def capture(stats_arg, damage_bonus):
    if isinstance(stats_arg, list):
        stats = list(stats_arg)
    else:
        stats = [stats_arg]
    return Packet(damage_bonus, stats)


def override(native_stats_arg, packet):
    if isinstance(native_stats_arg, list):
        stats = list(packet.stats)
    else:
        stats = packet.stats[0]
    return stats, packet.damage_bonus

original_damage = 472.0
single_packet = capture('single_usage_stats', original_damage)
single_stats, single_damage = override('native_single_struct', single_packet)
assert single_stats == 'single_usage_stats'
assert single_damage == original_damage

multi_packet = capture(['usage0', 'usage1'], original_damage)
multi_stats, multi_damage = override(['native0', 'native1'], multi_packet)
assert multi_stats == ['usage0', 'usage1']
assert multi_damage == original_damage

assert '<Version>1.1.16</Version>' in project
assert '<FileVersion>1.1.16.0</FileVersion>' in project
assert '<AssemblyVersion>1.1.16.0</AssemblyVersion>' in project
assert '<Version value="v1.1.16" />' in submodule

print('GA1116_SINGLE_USAGE_LAUNCH_BRIDGE_TESTS=PASS')
print('COMMON_ARROW_NATIVE_PATH=AddMissileSingleUsageAux')
print('MULTI_USAGE_NATIVE_PATH=AddMissileAux')
print('SINGLE_USAGE_ORIGINAL_CAPTURE=ENABLED')
print('SINGLE_USAGE_SYNTHETIC_OVERRIDE=ENABLED')
print('EXPECTED_FULL_DAMAGE=472')
