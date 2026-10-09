cmake_minimum_required(VERSION 3.21)

project(ArtDistributionTool VERSION 1.0.0 LANGUAGES CXX)

if (PROJECT_SOURCE_DIR STREQUAL PROJECT_BINARY_DIR)
	message(FATAL_ERROR "In-source builds are not allowed.")
endif ()

configure_file(${CMAKE_CURRENT_SOURCE_DIR}/cmake/Version.h.in ${CMAKE_CURRENT_BINARY_DIR}/include/Version.h @ONLY)
configure_file(${CMAKE_CURRENT_SOURCE_DIR}/cmake/version.rc.in ${CMAKE_CURRENT_BINARY_DIR}/version.rc @ONLY)
# Stamps every build with the commit it comes from (BuildStamp.h), checked on each build.
add_custom_target(BuildStamp
	COMMAND ${CMAKE_COMMAND} -DSOURCE=${CMAKE_CURRENT_SOURCE_DIR}/.. -DOUTPUT=${CMAKE_CURRENT_BINARY_DIR}/include/BuildStamp.h -P ${CMAKE_CURRENT_SOURCE_DIR}/cmake/BuildStamp.cmake
	BYPRODUCTS ${CMAKE_CURRENT_BINARY_DIR}/include/BuildStamp.h)

set(CMAKE_INTERPROCEDURAL_OPTIMIZATION ON)
set(CMAKE_INTERPROCEDURAL_OPTIMIZATION_DEBUG OFF)
set(Boost_USE_STATIC_LIBS ON)

if (NOT TARGET CommonLibF4)
	add_subdirectory("${CMAKE_CURRENT_SOURCE_DIR}/external/CommonLibF4RD/CommonLibF4" CommonLibF4)
endif ()

find_package(fmt REQUIRED CONFIG)
find_package(spdlog REQUIRED CONFIG)
find_package(nlohmann_json REQUIRED CONFIG)
find_package(libdeflate REQUIRED CONFIG)

set(SOURCES
	src/PCH.h
	src/main.cpp
	src/Plugin.cpp
	src/Plugin.h
	src/Settings.cpp
	src/Settings.h
	src/Catalog.cpp
	src/Catalog.h
	src/Packs.cpp
	src/Packs.h
	src/Engine.cpp
	src/Engine.h
	src/GameArchives.cpp
	src/GameArchives.h
	src/Distributor.cpp
	src/Distributor.h
	src/Papyrus.cpp
	src/Papyrus.h
	src/AutoTest.h
	src/ApplyNow.cpp
	src/ApplyNow.h
	src/LoadScreens.cpp
	src/LoadScreens.h
	src/MainMenu.cpp
	src/MainMenu.h
	src/MenuRotation.cpp
	src/MenuRotation.h
	src/Inventory.cpp
	src/Inventory.h
	src/CoreCheck.cpp
	src/CoreCheck.h
)

source_group(TREE ${CMAKE_CURRENT_SOURCE_DIR} FILES ${SOURCES})

# Builds the plugin from the shared sources plus any extra ones.
function(add_plugin target)
	add_library(${target} SHARED ${SOURCES} ${ARGN} ${CMAKE_CURRENT_BINARY_DIR}/version.rc)
	add_dependencies(${target} BuildStamp)
	target_compile_definitions(${target} PRIVATE _UNICODE)
	target_compile_features(${target} PRIVATE cxx_std_23)
	target_include_directories(${target} PRIVATE ${CMAKE_CURRENT_BINARY_DIR}/include ${CMAKE_CURRENT_SOURCE_DIR}/src)
	target_link_libraries(${target} PRIVATE CommonLibF4::CommonLibF4 fmt::fmt spdlog::spdlog nlohmann_json::nlohmann_json $<IF:$<TARGET_EXISTS:libdeflate::libdeflate_shared>,libdeflate::libdeflate_shared,libdeflate::libdeflate_static> bcrypt)
	target_precompile_headers(${target} PRIVATE src/PCH.h)
	if (MSVC)
		target_compile_options(${target} PRIVATE
			/sdl /utf-8 /Zi /permissive- /Zc:preprocessor /W4 /WX /external:W0
			"$<$<CONFIG:RELEASE>:/Zc:inline;/JMC->"
		)
		target_link_options(${target} PRIVATE
			/WX
			"$<$<CONFIG:DEBUG>:/INCREMENTAL;/OPT:NOREF;/OPT:NOICF>"
			"$<$<CONFIG:RELEASE>:/INCREMENTAL:NO;/OPT:REF;/OPT:ICF;/DEBUG:FULL>"
		)
	endif ()
endfunction()

# The plugin players get.
add_plugin(${PROJECT_NAME})

# The test build: the same sources plus the scripted test runs. It is written to Test/<config>/ArtDistributionTool.dll and is never packaged.
add_plugin(${PROJECT_NAME}Test src/AutoTest.cpp)
target_compile_definitions(${PROJECT_NAME}Test PRIVATE ADT_AUTOTEST)
set_target_properties(${PROJECT_NAME}Test PROPERTIES
	OUTPUT_NAME ${PROJECT_NAME}
	RUNTIME_OUTPUT_DIRECTORY ${CMAKE_CURRENT_BINARY_DIR}/Test
	ARCHIVE_OUTPUT_DIRECTORY ${CMAKE_CURRENT_BINARY_DIR}/Test
	PDB_OUTPUT_DIRECTORY ${CMAKE_CURRENT_BINARY_DIR}/Test
)
