add_executable(linux_smoke_test linux_smoke_test.cpp)
target_include_directories(linux_smoke_test PRIVATE
    ${CMAKE_SOURCE_DIR}
    ${CMAKE_SOURCE_DIR}/core
)
target_link_libraries(linux_smoke_test PRIVATE MFTLibNative)
set_target_properties(linux_smoke_test PROPERTIES
    BUILD_RPATH "$ORIGIN/.."
)
