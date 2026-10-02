cmake_minimum_required(VERSION 3.21)

project(GalleryShuffle VERSION 0.1.0 LANGUAGES CXX)

if (PROJECT_SOURCE_DIR STREQUAL PROJECT_BINARY_DIR)
	message(FATAL_ERROR "In-source builds are not allowed.")
endif ()

configure_file(${CMAKE_CURRENT_SOURCE_DIR}/cmake/Version.h.in ${CMAKE_CURRENT_BINARY_DIR}/include/Version.h @ONLY)
configure_file(${CMAKE_CURRENT_SOURCE_DIR}/cmake/version.rc.in ${CMAKE_CURRENT_BINARY_DIR}/version.rc @ONLY)

set(CMAKE_INTERPROCEDURAL_OPTIMIZATION ON)
set(CMAKE_INTERPROCEDURAL_OPTIMIZATION_DEBUG OFF)
set(Boost_USE_STATIC_LIBS ON)

if (NOT TARGET CommonLibF4)
	add_subdirectory("${CMAKE_CURRENT_SOURCE_DIR}/external/CommonLibF4RD/CommonLibF4" CommonLibF4)
endif ()

find_package(fmt REQUIRED CONFIG)
find_package(spdlog REQUIRED CONFIG)
find_package(nlohmann_json REQUIRED CONFIG)

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
	src/Distributor.cpp
	src/Distributor.h
	src/Papyrus.cpp
	src/Papyrus.h
	src/AutoTest.cpp
	src/AutoTest.h
	src/ApplyNow.cpp
	src/ApplyNow.h
	src/LoadScreens.cpp
	src/LoadScreens.h
	src/MainMenu.cpp
	src/MainMenu.h
	src/Sounds.cpp
	src/Sounds.h
	src/Inventory.cpp
	src/Inventory.h
)

source_group(TREE ${CMAKE_CURRENT_SOURCE_DIR} FILES ${SOURCES})

add_library(${PROJECT_NAME} SHARED ${SOURCES} ${CMAKE_CURRENT_BINARY_DIR}/version.rc)

target_compile_definitions(${PROJECT_NAME} PRIVATE _UNICODE)
target_compile_features(${PROJECT_NAME} PRIVATE cxx_std_23)
target_include_directories(${PROJECT_NAME} PRIVATE ${CMAKE_CURRENT_BINARY_DIR}/include ${CMAKE_CURRENT_SOURCE_DIR}/src)
target_link_libraries(${PROJECT_NAME} PRIVATE CommonLibF4::CommonLibF4 fmt::fmt spdlog::spdlog nlohmann_json::nlohmann_json)
target_precompile_headers(${PROJECT_NAME} PRIVATE src/PCH.h)

if (MSVC)
	target_compile_options(${PROJECT_NAME} PRIVATE
		/sdl /utf-8 /Zi /permissive- /Zc:preprocessor /W4 /WX /external:W0
		"$<$<CONFIG:RELEASE>:/Zc:inline;/JMC->"
	)
	target_link_options(${PROJECT_NAME} PRIVATE
		/WX
		"$<$<CONFIG:DEBUG>:/INCREMENTAL;/OPT:NOREF;/OPT:NOICF>"
		"$<$<CONFIG:RELEASE>:/INCREMENTAL:NO;/OPT:REF;/OPT:ICF;/DEBUG:FULL>"
	)
endif ()
