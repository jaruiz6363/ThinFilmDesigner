use strict; use warnings;
open my $h, "<:raw", "native/include/thinfilm.h" or die; local $/; my $s = <$h>; close $h;
$s =~ s{/\*.*?\*/}{}gs;

my %csname = (
  tf_status => 'TfStatus', tf_target_code => 'TfTargetCode', tf_variable_kind => 'TfVariableKind',
  tf_path => 'TfPath', tf_delta_convention => 'TfDeltaConvention', tf_reduction => 'TfReduction',
  tf_trial_reduction => 'TfTrialReduction', tf_extrapolation_policy => 'TfExtrapolationPolicy',
  tf_comparison => 'TfComparison', tf_polarisation_need => 'TfPolarisationNeed');

my $out = <<'HDR';
// Enums.cs — GENERATED from native/include/thinfilm.h. Do not hand-edit.
//
// Regenerate with tools/gen-managed-enums.pl when the ABI changes, which — the ABI being frozen —
// means in the same commit that bumps TF_ABI_VERSION, moves the pins in
// tests/native/test_abi_frozen.cpp and adds a change-log row to docs/scope.md.
//
// THE MEMBER NAMES ARE THE C NAMES. They are what the pins are keyed by, so the managed layout
// test can look each one up without a translation table — and a translation table is the thing
// that would silently go stale. The unusual C# casing is the price, and it is cheap: only the
// Design layer above this one is meant to be read as C#.

namespace ThinFilm.Native;

HDR

for my $c (sort keys %csname)
{
    next unless $s =~ /typedef\s+enum\s+$c\s*\{(.*?)\}\s*$c\s*;/s;
    my $body = $1;
    my @pairs;
    while ($body =~ /\b(TF_\w+)\s*=\s*(-?\d+)/g) { push @pairs, [$1, $2]; }
    next unless @pairs;
    $out .= "public enum $csname{$c} : int\n{\n";
    $out .= sprintf("    %-33s = %s,\n", $_->[0], $_->[1]) for @pairs;
    $out =~ s/,\n$/\n/;
    $out .= "}\n\n";
}

# The one loose constant that is not in an enum.
if ($s =~ /#define\s+TF_ABI_VERSION\s+(\d+)/) {
    $out .= "/// <summary>Loose #defines from the header that are part of the ABI.</summary>\n";
    $out .= "public static class TfAbi\n{\n";
    $out .= "    public const int TF_ABI_VERSION = $1;\n";
    while ($s =~ /#define\s+(TF_FLAG_\w+)\s+(\d+)/g) { $out .= "    public const int $1 = $2;\n"; }
    while ($s =~ /#define\s+(TF_DELTA_IMAG_CLAMP|TF_OD_FLOOR_DEFAULT)\s+([0-9.]+)/g) {
        $out .= "    public const double $1 = $2;\n"; }
    $out .= "}\n";
}

open my $o, ">:raw", "managed/ThinFilm.Native/Enums.cs" or die;
print $o $out; close $o;
my $n = () = ($out =~ /^    TF_\w+\s+=/gm);
print "generated $n enumerators\n";
