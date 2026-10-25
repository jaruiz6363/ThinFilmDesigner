# ThinFilm — native evaluation engine.
#
# Cross-platform is a goal of the project, so the build is CMake from the start rather than a
# Visual Studio solution retrofitted later.

cmake_minimum_required(VERSION 3.20)
project(thinfilm LANGUAGES C CXX)

set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Warnings are errors. The evaluator is arithmetic that has to be right; a warning here is a
# defect report and treating it as advisory would waste it.
if(MSVC)
  add_compile_options(/W4 /WX)
else()
  add_compile_options(-Wall -Wextra -Wpedantic -Werror)
endif()

add_library(thinfilm STATIC
  native/src/tf_plan.cpp
  native/src/tf_evaluate.cpp
  native/src/tf_jacobian.cpp
  native/src/tf_spectral.cpp
  native/src/tf_needle.cpp
  native/src/tf_index_derivative.cpp
  native/src/tf_dispersion.cpp)

# THE SHARED LIBRARY THE MANAGED LAYER LOADS.
#
# The same sources as the static one, built again rather than wrapped, because a static library
# cannot be P/Invoked and an object library would still need the export table. TF_API is already
# __declspec(dllexport) on Windows and default visibility elsewhere, so nothing in the sources
# changes - only the link.
#
# Both targets exist on purpose: the native tests link the static one, so they test the same
# translation units the DLL is built from and cannot drift onto a different build of the engine.
add_library(thinfilm_shared SHARED
  native/src/tf_plan.cpp
  native/src/tf_evaluate.cpp
  native/src/tf_jacobian.cpp
  native/src/tf_spectral.cpp
  native/src/tf_needle.cpp
  native/src/tf_index_derivative.cpp
  native/src/tf_dispersion.cpp)

# NAMED thinfilm_native, NOT thinfilm, AND THE COLLISION WAS REAL. The CLI assembly is named
# thinfilm so that its executable is `thinfilm`, and .NET emits thinfilm.dll alongside
# thinfilm.exe - which P/Invoke then finds first and reports as a library with no entry points.
# Two artefacts cannot share a name, and the one that should give way is the library nobody types.
set_target_properties(thinfilm_shared PROPERTIES OUTPUT_NAME thinfilm_native)

target_include_directories(thinfilm_shared PUBLIC
  ${CMAKE_CURRENT_SOURCE_DIR}/native/include
  ${CMAKE_CURRENT_SOURCE_DIR}/native/src)

add_library(thinfilm_headers INTERFACE)

target_include_directories(thinfilm_headers INTERFACE
  ${CMAKE_CURRENT_SOURCE_DIR}/native/include
  ${CMAKE_CURRENT_SOURCE_DIR}/native/src)

target_include_directories(thinfilm PUBLIC
  ${CMAKE_CURRENT_SOURCE_DIR}/native/include
  ${CMAKE_CURRENT_SOURCE_DIR}/native/src)

enable_testing()

add_executable(test_matrix2 tests/native/test_matrix2.cpp)
target_link_libraries(test_matrix2 PRIVATE thinfilm_headers)
add_test(NAME matrix2 COMMAND test_matrix2)

add_executable(test_plan tests/native/test_plan.cpp)
target_link_libraries(test_plan PRIVATE thinfilm)
add_test(NAME plan COMMAND test_plan)

add_executable(test_evaluate tests/native/test_evaluate.cpp)
target_link_libraries(test_evaluate PRIVATE thinfilm)
add_test(NAME evaluate COMMAND test_evaluate)

add_executable(test_jacobian tests/native/test_jacobian.cpp)
target_link_libraries(test_jacobian PRIVATE thinfilm)
add_test(NAME jacobian COMMAND test_jacobian)

add_executable(test_spectral tests/native/test_spectral.cpp)
target_link_libraries(test_spectral PRIVATE thinfilm)
add_test(NAME spectral COMMAND test_spectral)

add_executable(test_dispersion tests/native/test_dispersion.cpp)
target_link_libraries(test_dispersion PRIVATE thinfilm)
add_test(NAME dispersion COMMAND test_dispersion)

add_executable(test_needle tests/native/test_needle.cpp)
target_link_libraries(test_needle PRIVATE thinfilm)
add_test(NAME needle COMMAND test_needle)

add_executable(test_index_derivative tests/native/test_index_derivative.cpp)
target_link_libraries(test_index_derivative PRIVATE thinfilm)
add_test(NAME index_derivative COMMAND test_index_derivative)

add_executable(test_comparison tests/native/test_comparison.cpp)
target_link_libraries(test_comparison PRIVATE thinfilm)
add_test(NAME comparison COMMAND test_comparison)

add_executable(test_reduction tests/native/test_reduction.cpp)
target_link_libraries(test_reduction PRIVATE thinfilm)
add_test(NAME reduction COMMAND test_reduction)

add_executable(test_configuration tests/native/test_configuration.cpp)
target_link_libraries(test_configuration PRIVATE thinfilm)
add_test(NAME configuration COMMAND test_configuration)

add_executable(test_derived tests/native/test_derived.cpp)
target_link_libraries(test_derived PRIVATE thinfilm)
add_test(NAME derived COMMAND test_derived)

add_executable(test_crossing tests/native/test_crossing.cpp)
target_link_libraries(test_crossing PRIVATE thinfilm)
add_test(NAME crossing COMMAND test_crossing)

add_executable(test_span_derivatives tests/native/test_span_derivatives.cpp)
target_link_libraries(test_span_derivatives PRIVATE thinfilm)
add_test(NAME span_derivatives COMMAND test_span_derivatives)

add_executable(test_fixtures tests/native/test_fixtures.cpp)
target_link_libraries(test_fixtures PRIVATE thinfilm)
add_test(NAME fixtures COMMAND test_fixtures WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR})

add_executable(test_abi_frozen tests/native/test_abi_frozen.cpp)
target_link_libraries(test_abi_frozen PRIVATE thinfilm)
add_test(NAME abi_frozen COMMAND test_abi_frozen)

add_executable(test_trials tests/native/test_trials.cpp)
target_link_libraries(test_trials PRIVATE thinfilm)
add_test(NAME trials COMMAND test_trials)

add_executable(test_material_data tests/native/test_material_data.cpp)
target_link_libraries(test_material_data PRIVATE thinfilm)
add_test(NAME material_data COMMAND test_material_data)

add_executable(test_phase1_targets tests/native/test_phase1_targets.cpp)
target_link_libraries(test_phase1_targets PRIVATE thinfilm)
add_test(NAME phase1_targets COMMAND test_phase1_targets)

add_executable(test_stack_operands tests/native/test_stack_operands.cpp)
target_link_libraries(test_stack_operands PRIVATE thinfilm)
add_test(NAME stack_operands COMMAND test_stack_operands)

# The scope ledger, enforced: every docs/scope.md row marked REFUSES must still refuse.
add_executable(test_scope tests/native/test_scope.cpp)
target_link_libraries(test_scope PRIVATE thinfilm)
add_test(NAME scope COMMAND test_scope)

# The standing constraints of docs/scope.md section 0 - no fixed arrays, no float, TF_HD
# everywhere, and a ledger that has kept up with the ABI.
add_test(NAME constraints COMMAND "${CMAKE_COMMAND}"
         "-DTF_ROOT=${CMAKE_CURRENT_SOURCE_DIR}"
         -P "${CMAKE_CURRENT_SOURCE_DIR}/tools/check-constraints.cmake")
