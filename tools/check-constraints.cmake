# check-constraints.cmake — the standing constraints of docs/scope.md section 0, checked by the
# build rather than remembered.
#
# Run as a test:  cmake -DTF_ROOT=<repo> -P tools/check-constraints.cmake
#
# These are the conditions the project owner set on the whole engine. They are the kind of rule
# that is obeyed for months and then quietly broken by one convenient edit, and the break is
# invisible until the CUDA port or a precision problem surfaces it. So they are checked, not
# trusted. CMake script rather than a shell script because the project is cross-platform from the
# start and this must run wherever the build does.

if(NOT DEFINED TF_ROOT)
  get_filename_component(TF_ROOT "${CMAKE_CURRENT_LIST_DIR}/.." ABSOLUTE)
endif()

set(FAILURES 0)

function(fail id msg)
  message("  FAIL  [${id}] ${msg}")
  math(EXPR FAILURES "${FAILURES}+1")
  set(FAILURES ${FAILURES} PARENT_SCOPE)
endfunction()

file(GLOB_RECURSE NATIVE_SOURCES
     "${TF_ROOT}/native/include/*.h"
     "${TF_ROOT}/native/src/*.h"
     "${TF_ROOT}/native/src/*.cpp")

# ---------------------------------------------------------------------------------------------
# C0.2  No fixed static arrays dimensioned by a constant. Expressly forbidden.
#
# The pattern that matters is a compile-time bound used as an array size — MAX_NUMBER_SURFACES and
# its relatives. Catching the #define catches the whole family, because an array cannot be sized by
# a constant that does not exist.
# ---------------------------------------------------------------------------------------------
foreach(f ${NATIVE_SOURCES})
  file(STRINGS "${f}" lines)
  set(prev "")
  foreach(line ${lines})
    if(line MATCHES "^[ \t]*#[ \t]*define[ \t]+MAX_[A-Z0-9_]+[ \t]+[0-9]")
      fail("C0.2" "${f}: a MAX_ constant — fixed arrays are forbidden: ${line}")
    endif()
    # A DECLARATION of an array with a literal bound - "type name[8];" - and not merely an
    # A DECLARATION of an array with a literal bound - "type name[8];" - not an indexing
    # expression. The earlier form also matched "double m = x[0];", which reads element zero of a
    # caller's buffer and declares nothing; a checker that cries wolf gets switched off. The
    # brackets are written as "." to keep this out of CMake's escaping rules entirely - with a
    # type, a name, digits and a closing semicolon all required, nothing else matches.
    if(line MATCHES "^[ \t]*(const[ \t]+)?[A-Za-z_][A-Za-z0-9_]*[ \t]+[A-Za-z_][A-Za-z0-9_]*[ \t]*.[ \t]*[0-9]+[ \t]*.[ \t]*;")
      if(NOT line MATCHES "//" AND NOT line MATCHES "^[ \t]*[*]" AND NOT line MATCHES "TF_FIXED_OK" AND NOT prev MATCHES "TF_FIXED_OK")
        fail("C0.2" "${f}: a fixed-size array with no TF_FIXED_OK justification: ${line}")
      endif()
    endif()
    set(prev "${line}")
  endforeach()
endforeach()

# ---------------------------------------------------------------------------------------------
# C0.4  Double precision. Float was tried in the legacy code and was not good enough.
# ---------------------------------------------------------------------------------------------
foreach(f ${NATIVE_SOURCES})
  file(STRINGS "${f}" lines)
  foreach(line ${lines})
    if(line MATCHES "(^|[^a-zA-Z0-9_])float([^a-zA-Z0-9_]|$)")
      if(NOT line MATCHES "^[ \t]*[*/]" AND NOT line MATCHES "//")
        fail("C0.4" "${f}: float appears — this engine is double throughout: ${line}")
      endif()
    endif()
  endforeach()
endforeach()

# ---------------------------------------------------------------------------------------------
# C0.3  The CUDA port must stay simple.
#
# Every inline function in the three inner-loop headers must carry TF_HD, or it cannot be called
# from device code and the port stops being a port. This is the one constraint of the six that a
# single careless edit breaks silently, and it is fully mechanical, so it is checked exactly.
# ---------------------------------------------------------------------------------------------
foreach(h tf_complex_math.h tf_matrix2.h tf_layer.h)
  file(STRINGS "${TF_ROOT}/native/src/${h}" lines)
  foreach(line ${lines})
    if(line MATCHES "^(static )?inline ")
      fail("C0.3" "${h}: an inline without TF_HD — device code could not call it: ${line}")
    endif()
  endforeach()
endforeach()

# ---------------------------------------------------------------------------------------------
# The ledger must keep up with the ABI.
#
# An ABI bump that does not appear in the scope ledger's change log is exactly the drift this whole
# apparatus exists to catch: the code moved and the record of intent did not.
# ---------------------------------------------------------------------------------------------
if(NOT EXISTS "${TF_ROOT}/docs/scope.md")
  fail("ledger" "docs/scope.md is missing — the record of intent must exist")
else()
  file(READ "${TF_ROOT}/native/include/thinfilm.h" HEADER)
  file(READ "${TF_ROOT}/docs/scope.md" LEDGER)

  if(HEADER MATCHES "#define TF_ABI_VERSION ([0-9]+)")
    set(ABI "${CMAKE_MATCH_1}")
    if(NOT LEDGER MATCHES "\n\\| *${ABI} *\\|")
      fail("ledger" "TF_ABI_VERSION is ${ABI} and docs/scope.md has no change-log row for it")
    endif()
  else()
    fail("ledger" "TF_ABI_VERSION not found in thinfilm.h")
  endif()

  # PLANNED is legitimate only above the ABI. Inside native an unbuilt branch must REFUSE.
  string(FIND "${LEDGER}" "## 1. Native engine" NATIVE_AT)
  string(FIND "${LEDGER}" "## 2. Managed side" MANAGED_AT)
  if(NATIVE_AT GREATER 0 AND MANAGED_AT GREATER NATIVE_AT)
    math(EXPR NATIVE_LEN "${MANAGED_AT}-${NATIVE_AT}")
    string(SUBSTRING "${LEDGER}" ${NATIVE_AT} ${NATIVE_LEN} NATIVE_PART)
    if(NATIVE_PART MATCHES "PLANNED")
      fail("ledger" "a native item is marked PLANNED — inside native, unbuilt must mean REFUSES")
    endif()
  endif()

  # The ABI may not be declared frozen while part of the specification still has nowhere to land.
  #
  # This is the rule that protects the project from its own staging. A NO SEAT row is a feature the
  # ABI cannot express, so nothing can refuse it and nothing announces it is missing. Binding C# to
  # an ABI in that state is how a staged step turns into a scope reduction: the wrapper hardens
  # around what exists, and the missing feature becomes an ABI break plus a rewrite of everything
  # above it. So freezing is gated on the count reaching zero.
  # BOTH HALVES OF THIS TEST USED TO BE SUBSTRING MATCHES OVER THE WHOLE DOCUMENT, and both
  # were wrong in opposite directions.
  #
  # The flag matched anywhere, so a sentence ABOUT the flag set it. And "NO SEAT" matched anywhere
  # too - including the legend near the top, which defines what a NO SEAT row IS and therefore
  # contains those words permanently. That second one is the serious half: the gate could never
  # have opened, whatever the ledger said, because the definition of the thing being counted was
  # itself counted. A gate that cannot open is indistinguishable from a gate that is holding, and
  # it would have been discovered as a puzzle at exactly the wrong moment.
  #
  # So the flag is read from its own declaration line, and the rows are counted by their MARKUP -
  # a state cell is bolded, and the legend row is not a state cell.
  string(REGEX MATCH "\n[ \t]+ABI_FROZEN:[ \t]*([A-Za-z]+)" FROZEN_LINE "${LEDGER}")
  set(FROZEN_VALUE "${CMAKE_MATCH_1}")

  # NOT a boolean test: CMake treats the string "no" as FALSE, so `if(NOT FROZEN_VALUE)` fires on
  # a flag that was read perfectly well and says no. Compare the text.
  if(FROZEN_VALUE STREQUAL "")
    fail("ledger" "docs/scope.md has no ABI_FROZEN declaration line - the freeze gate cannot be read")
  elseif(FROZEN_VALUE STREQUAL "yes")
    # A LITERAL REPLACE, NOT A REGEX. The marker is "**NO SEAT**" and every one of those
    # asterisks is a quantifier to a regex engine; escaping them correctly through both CMake
    # string parsing and the regex is the kind of thing that is wrong in a way nobody notices,
    # which is exactly the failure this whole gate exists to prevent. REPLACE is literal.
    string(REPLACE "**NO SEAT**" "<SEATGAP>" SEAT_MARKED "${LEDGER}")
    string(REGEX MATCHALL "<SEATGAP>" SEAT_ROWS "${SEAT_MARKED}")
    list(LENGTH SEAT_ROWS SEAT_COUNT)
    # The legend itself carries one bolded **NO SEAT**, as the label of the row that defines the
    # state. That one is the definition, not an outstanding item.
    if(SEAT_COUNT GREATER 1)
      fail("ledger" "ABI_FROZEN is yes while NO SEAT rows remain - the ABI cannot be frozen while part of the specification has no way to be expressed")
    endif()
  endif()
endif()

# ---------------------------------------------------------------------------------------------

if(FAILURES GREATER 0)
  message(FATAL_ERROR "constraint check: ${FAILURES} failure(s)")
endif()
message("constraint check: all standing constraints hold")
