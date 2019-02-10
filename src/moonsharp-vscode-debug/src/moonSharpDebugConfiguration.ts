'use strict'

import * as vscode from 'vscode'

export type PortProvidedCallback = (int) => void

export class MoonSharpDebugConfigurationProvider implements vscode.DebugConfigurationProvider {

	onPortProvided: PortProvidedCallback

	constructor(onPortProvided: PortProvidedCallback)
	{
		this.onPortProvided = onPortProvided
	}

	public provideDebugConfigurations(folder: vscode.WorkspaceFolder | undefined, token?: vscode.CancellationToken): vscode.ProviderResult<vscode.DebugConfiguration[]> {
		const path = folder ? folder.uri.fsPath : '${fileDirname}'
		return [
			{
				type: 'moonsharp-lua',
				name: 'Master',
				request: 'attach',
				path,
			},
			{
				type: 'moonsharp-lua',
				name: 'Launch',
				request: 'launch',
				mode:  'directory', // 'file' or 'directory', the later scans for all *.(lua|ttslua) files in 'path'.
				pipeline: [], // Pipeline is made of stages that modify a (temporary) file. Each stage has either an extension 'command' or an 'externalCommand'.
				path,
			}
		]
	}

	public async resolveDebugConfiguration?(folder: vscode.WorkspaceFolder | undefined, debugConfiguration: vscode.DebugConfiguration, token?: vscode.CancellationToken): Promise<vscode.DebugConfiguration | null> {
		const activeEditor = vscode.window.activeTextEditor

		if ((!debugConfiguration || debugConfiguration.type !== 'moonsharp-lua') && (!activeEditor || activeEditor.document.languageId !== 'lua')) {
			return debugConfiguration
		}

		if (!debugConfiguration.debugServer) {
			const inputPort = await vscode.window.showInputBox({
				value: '41912',
				prompt: 'MoonSharp server port',
				placeHolder: 'Port',
				validateInput: (value: string) => {
					const port = parseInt(value)
					return (port > 0 && port <= 65535) ? null : 'Not a valid port number'
				},
				ignoreFocusOut: true,
			})

			if (!inputPort) {
				return null
			}

			debugConfiguration.debugServer = parseInt(inputPort)
		}

		if (!debugConfiguration.slave && this.onPortProvided) {
			this.onPortProvided(debugConfiguration.debugServer)
		}

		return {
			name: 'Global',
			request: 'attach',
			mode: 'directory',
			pipeline: [],
			path: folder ? folder.uri.fsPath : (activeEditor ? activeEditor.document.uri.fsPath : null),
			...debugConfiguration,
			type: 'moonsharp-lua',
		}
	}
}
