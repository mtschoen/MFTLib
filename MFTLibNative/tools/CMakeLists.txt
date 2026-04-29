add_executable(mft-cli mft-cli.cpp)
target_include_directories(mft-cli PRIVATE
    ${CMAKE_SOURCE_DIR}
    ${CMAKE_SOURCE_DIR}/core
)
target_link_libraries(mft-cli PRIVATE MFTLibNative)
set_target_properties(mft-cli PROPERTIES
    BUILD_RPATH "$ORIGIN/.."
)
