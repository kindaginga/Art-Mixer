#pragma once

// The game's own textures and models, read from the game's archives past any mod's files.
namespace ArtMixer::GameArchives
{
	// Names the game archive (file name in Data) each game file the packs use is read from; textures are lowercase paths relative
	// to Textures, models lowercase paths relative to Meshes.
	void SetSources(const std::unordered_map<std::string, std::string>& a_textures, const std::unordered_map<std::string, std::string>& a_models);
	// Names the game archive one more game file (lowercase path relative to Data) is read from, such as the loading menu's Flash file.
	void AddSource(const std::string& a_file, const std::string& a_archive);
	// Registers the file source that serves a game file under textures\artmixer\game\<its path> or meshes\artmixer\game\<its path>.
	bool Install();
}
